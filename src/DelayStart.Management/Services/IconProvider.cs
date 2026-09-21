using System.Collections.Concurrent;

using DelayStart.Management.Interop;
using DelayStart.Management.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 按解析名提取程序图标（D30，docs/design.md 7.5）。
/// </summary>
/// <remarks>
/// <para>
/// 输入是"解析名"而不是抽象的"条目"：<c>SHCreateItemFromParsingName</c> 一个入口
/// 就接受文件路径、<c>.lnk</c> 快捷方式与 <c>shell:AppsFolder\&lt;AUMID&gt;</c> 三种形态，
/// 调用方（ViewModel）知道自己手里是什么，不需要这里再造一层分发。
/// </para>
/// <para>
/// 🔴 <b>失败返回 <see langword="null"/>，绝不抛异常、也不记日志</b>：图标是装饰品，
/// "这个文件已经不存在了"这件事列表本身就会以「已失效」标出。项目日志刻意只有
/// Info / Warn / Error 三档（<c>LogLevel</c> 的注释），没有能容纳"常态失败"的级别 ——
/// 37 项里混几个失效项每次扫描都 Warn，2 MB 的日志上限会被它独占。
/// </para>
/// <para>
/// 缓存只在**成功**时写入：失败（文件被短暂占用等）允许下次刷新重试；
/// 而成功结果按解析名 + 尺寸缓存 —— 扫描刷新、页面切换、条目编辑都会反复
/// 请求同一批图标，每次都走 <c>SHCreateItemFromParsingName</c> 是肉眼可见的卡顿。
/// </para>
/// </remarks>
public sealed class IconProvider
{
    /// <summary>标准提取尺寸（物理像素）。</summary>
    /// <remarks>
    /// 列表行图标显示为 32 DIP；64px 覆盖到 200% DPI（R7 的最高档），
    /// 更高的 DPI 由 XAML 的 <c>Stretch=Uniform</c> 降采样，不会再糊。
    /// </remarks>
    public const int StandardPixelSize = 64;

    private readonly ConcurrentDictionary<string, IconPixels> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取一个解析名的图标像素；失败时为 <see langword="null"/>。</summary>
    /// <param name="parsingName">文件路径 / <c>.lnk</c> / <c>shell:AppsFolder\&lt;AUMID&gt;</c>。</param>
    /// <param name="pixelSize">请求的像素尺寸；传 0 或负数取 <see cref="StandardPixelSize"/>。</param>
    /// <returns>像素数据；取不到时为 <see langword="null"/>。</returns>
    public IconPixels? TryGetIcon(string parsingName, int pixelSize = StandardPixelSize)
    {
        if (string.IsNullOrWhiteSpace(parsingName))
        {
            return null;
        }

        if (pixelSize <= 0)
        {
            pixelSize = StandardPixelSize;
        }

        var key = $"{pixelSize}|{parsingName}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = Extract(parsingName, pixelSize);
        if (icon is not null)
        {
            // 并发下同一键可能被提取两次（双页面同时首扫），后写覆盖先写 ——
            // 两者内容等价，不值得为省一次提取引入锁。
            _cache[key] = icon;
        }

        return icon;
    }

    /// <summary>清空缓存。</summary>
    /// <remarks>目前没有调用方 —— 保留给将来"图标刷新"设置用；缓存类不配清空方法
    /// 迟早变成"只能靠重启清缓存"。</remarks>
    public void Clear() => _cache.Clear();

    /// <summary>实际提取：创建工厂 → 要 HBITMAP → 读像素 → 逐层释放。</summary>
    /// <param name="parsingName">解析名。</param>
    /// <param name="pixelSize">像素尺寸。</param>
    /// <returns>像素数据；任一步失败为 <see langword="null"/>。</returns>
    private static IconPixels? Extract(string parsingName, int pixelSize)
    {
        var factory = ShellItemImageFactoryNative.TryCreateFactory(parsingName);
        if (factory is null)
        {
            return null;
        }

        try
        {
            var hr = factory.GetImage(
                new InteropSize(pixelSize, pixelSize),
                ShellItemImageFactoryFlags.IconOnly | ShellItemImageFactoryFlags.BiggerSizeOk,
                out var hBitmap);

            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var pixels = GdiInterop.TryGetPixels(hBitmap, pixelSize, pixelSize);
                return pixels is null ? null : new IconPixels(pixels, pixelSize, pixelSize);
            }
            finally
            {
                // HBITMAP 不释放就是 GDI 句柄泄漏（进程共享 10000 个上限 ——
                // 每次扫描 37 个，刷 20 次就见底）。finally 保证任何路径都释放。
                GdiInterop.DeleteBitmap(hBitmap);
            }
        }
        finally
        {
            _ = System.Runtime.InteropServices.Marshal.ReleaseComObject(factory);
        }
    }
}
