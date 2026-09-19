using DelayStart.Management.Models;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DelayStart.App.Services;

/// <summary>
/// 把 <see cref="IconPixels"/> 转成 XAML 可显示的 <see cref="ImageSource"/>。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 用 <see cref="WriteableBitmap"/> 而不是 <c>SoftwareBitmapSource</c>：
/// 后者灌位图要走 <c>SetBitmapAsync</c>，而列表行的构建在 UI 线程同步路径上 ——
/// 为了 37 个图标引入 37 个 await，刷新瞬间会出现"行先出现、图标逐个补上"的闪变。
/// <c>WriteableBitmap.PixelBuffer</c> 允许同步写入，一次 <see cref="WriteableBitmap.Invalidate"/>
/// 就完成显示，行与图标同帧出现。
/// </para>
/// <para>
/// 输入的 BGRA 布局与 <c>WriteableBitmap</c> 的 Bgra8 布局逐字节一致，
/// 拷贝就是全部工作 —— 尺寸不等时才需要重采样，而提取层保证了等尺寸
/// （请求多大、<c>GetImage</c> 给多大，<c>BI_RGB</c> 32bpp 原样读出）。
/// </para>
/// </remarks>
public static class IconRenderer
{
    /// <summary>像素转图像源。</summary>
    /// <param name="pixels">提取到的像素；<see langword="null"/> 表示该条目没有可用图标。</param>
    /// <returns>可直接赋给 <c>Image.Source</c> 的对象；输入为 <see langword="null"/> 时返回 <see langword="null"/>。</returns>
    /// <remarks>**必须在 UI 线程调用**（<see cref="WriteableBitmap"/> 是 XAML 对象）。</remarks>
    public static ImageSource? ToImageSource(IconPixels? pixels)
    {
        if (pixels is null)
        {
            return null;
        }

        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);

        // CsWinRT 为 IBuffer 提供 WindowsRuntimeBufferExtensions（CopyTo / AsStream），
        // 与 UWP 时代的 System.Runtime.InteropServices.WindowsRuntime 同名同语义。
        System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions
            .CopyTo(pixels.Bgra, bitmap.PixelBuffer);

        bitmap.Invalidate();
        return bitmap;
    }
}
