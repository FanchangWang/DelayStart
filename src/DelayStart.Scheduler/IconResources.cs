using System.Reflection;

namespace DelayStart.Scheduler;

/// <summary>
/// 嵌入资源图标的加载（<c>scheduler-design.md</c> 六：禁止 <c>.resx</c>，AOT 下加载必失败）。
/// </summary>
/// <remarks>
/// <para>
/// 图标以普通 <c>EmbeddedResource</c> 嵌入 .ico 文件，加载时手工解析 ICO 容器
/// （目录 + 条目），把选中的位图条目交给 <c>CreateIconFromResourceEx</c> 生成 HICON。
/// 全程零反射、零 COM。
/// </para>
/// <para>
/// HICON 由 <see cref="Dispose"/> 释放；实例由程序生命周期持有，不依赖 GC 终结器。
/// </para>
/// </remarks>
internal sealed class IconResources : IDisposable
{
    /// <summary>
    /// 托盘图标请求的 HICON 边长（像素）。
    /// </summary>
    /// <remarks>
    /// 🔴 刻意**不**用 <c>GetSystemMetrics(SM_CXSMICON)</c>：本进程没有 DPI 感知声明，
    /// 那个值恒为 16，在 150%/200% 缩放下等于把 16px 的位图交给外壳放大 —— 糊。
    /// 取 32 的实际效果：100% 由外壳缩到 16（1:2 整数比，干净）、150% 缩到 24、
    /// 200% 直接用满 32。即"发一个偏大的位图让外壳往下缩"，是托盘图标的常规做法。
    /// </remarks>
    private const int TrayIconSize = 32;

    private readonly nint _normal;
    private readonly nint _warning;

    private IconResources(nint normal, nint warning)
    {
        _normal = normal;
        _warning = warning;
    }

    /// <summary>正常图标（调度中）。</summary>
    public nint Normal => _normal;

    /// <summary>告警图标（完成但有失败 —— 红色角标）。</summary>
    public nint Warning => _warning;

    /// <summary>从程序集嵌入资源加载两个图标。任一失败返回 <see langword="null"/>（托盘随设置为可关闭项，不致命）。</summary>
    /// <param name="assembly">包含嵌入资源的程序集。</param>
    /// <param name="normalResourceName">正常图标资源全名。</param>
    /// <param name="warningResourceName">告警图标资源全名。</param>
    public static IconResources? Load(Assembly assembly, string normalResourceName, string warningResourceName)
    {
        var normal = LoadSingleIcon(assembly, normalResourceName);
        var warning = LoadSingleIcon(assembly, warningResourceName);

        return normal != 0 && warning != 0 ? new IconResources(normal, warning) : null;
    }

    /// <summary>调度完成后应使用的图标（有失败时切告警角标）。</summary>
    /// <param name="hasFailure">是否存在失败条目。</param>
    /// <returns>图标句柄。</returns>
    public nint Pick(bool hasFailure) => hasFailure ? _warning : _normal;

    public void Dispose()
    {
        if (_normal != 0)
        {
            _ = NativeMethods.DestroyIcon(_normal);
        }

        if (_warning != 0)
        {
            _ = NativeMethods.DestroyIcon(_warning);
        }
    }

    private static nint LoadSingleIcon(Assembly assembly, string resourceName)
    {
        try
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                return 0;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return CreateIconFromIcoBytes(buffer.ToArray());
        }
        catch (Exception)
        {
            // 图标加载失败只影响托盘可视化，不值得让调度流程失败。
            return 0;
        }
    }

    /// <summary>解析 ICO 容器并生成 HICON：挑一枚最贴合托盘尺寸的条目。</summary>
    /// <remarks>
    /// 🔴 不要"取第一个条目"。图标资源是 10 档的多尺寸容器（<c>tools/make-icon.py</c> 生成），
    /// 目录按尺寸升序排，第一个是 16×16；配上 <c>LR_DEFAULTSIZE</c> 会被系统放大到 32×32、
    /// 再被外壳缩回 16×16 —— 两次重采样，肉眼可见地糊。这里按目标尺寸挑条目并把
    /// cx/cy 显式传下去，全程只重采样一次（或一次都不需要）。
    /// </remarks>
    private static unsafe nint CreateIconFromIcoBytes(byte[] icoBytes)
    {
        if (icoBytes.Length < 6)
        {
            return 0;
        }

        var entryCount = ReadUInt16(icoBytes, 4);
        if (entryCount < 1 || icoBytes.Length < 6 + (16 * entryCount))
        {
            return 0;
        }

        var entryIndex = PickEntryIndex(icoBytes, entryCount, out var entrySize);
        if (entryIndex < 0)
        {
            return 0;
        }

        // ICONDIRENTRY 从偏移 6 开始，每条 16 字节：+8 = 数据长度，+12 = 数据偏移。
        var directoryOffset = 6 + (16 * entryIndex);
        var dataOffset = ReadUInt32(icoBytes, directoryOffset + 12);
        var dataLength = ReadUInt32(icoBytes, directoryOffset + 8);
        if (dataOffset + dataLength > icoBytes.Length)
        {
            return 0;
        }

        fixed (byte* bits = icoBytes.AsSpan((int)dataOffset, (int)dataLength))
        {
            return NativeMethods.CreateIconFromResourceEx(
                bits,
                dataLength,
                icon: true,
                0x00030000,
                entrySize,
                entrySize,
                0);
        }
    }

    /// <summary>
    /// 在 ICO 目录里挑最合适的条目：优先"不超过目标尺寸里最大的那枚"，都没有则取最小的那枚。
    /// </summary>
    /// <param name="icoBytes">完整 ICO 字节。</param>
    /// <param name="entryCount">目录条目数。</param>
    /// <param name="entrySize">选中的边长（正方形，取宽高中较小者）。</param>
    /// <returns>目录下标；无法解析时 -1。</returns>
    private static int PickEntryIndex(byte[] icoBytes, int entryCount, out int entrySize)
    {
        var bestFitIndex = -1;
        var bestFitSize = 0;
        var smallestIndex = -1;
        var smallestSize = int.MaxValue;

        for (var i = 0; i < entryCount; i++)
        {
            var offset = 6 + (16 * i);

            // ICO 的宽高字段各占一个字节，装不下 256 —— 0 就表示 256。
            var width = icoBytes[offset] == 0 ? 256 : icoBytes[offset];
            var height = icoBytes[offset + 1] == 0 ? 256 : icoBytes[offset + 1];
            var size = Math.Min(width, height);

            if (size <= TrayIconSize && size > bestFitSize)
            {
                bestFitSize = size;
                bestFitIndex = i;
            }

            if (size < smallestSize)
            {
                smallestSize = size;
                smallestIndex = i;
            }
        }

        if (bestFitIndex >= 0)
        {
            entrySize = bestFitSize;
            return bestFitIndex;
        }

        entrySize = smallestIndex >= 0 ? smallestSize : 0;
        return smallestIndex;
    }

    private static ushort ReadUInt16(byte[] data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadUInt32(byte[] data, int offset)
        => (uint)(data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24));
}
