namespace DelayStart.Management.Models;

/// <summary>
/// 提取到的图标像素数据（32 位 BGRA，自上而下行序）。
/// </summary>
/// <remarks>
/// <para>
/// 刻意用"裸像素"而不是 <c>HBITMAP</c> 或 PNG 字节流跨层传递：
/// </para>
/// <list type="bullet">
/// <item><description><c>HBITMAP</c> 是 GDI 句柄，跨层传递后释放责任说不清 —— 忘了
/// <c>DeleteObject</c> 就是 GDI 句柄泄漏，37 行列表能漏出 37 个。</description></item>
/// <item><description>PNG 字节流需要在 Management 里引入图像编码器（System.Drawing 或 WIC），
/// 而 App 侧显示前还得解码一次 —— 两次编解码纯属浪费。</description></item>
/// <item><description>裸像素在 App 侧可以直接灌进 <c>WriteableBitmap.PixelBuffer</c>，
/// 一次拷贝就显示，且无 UI 线程亲和性问题（字节本身不是 XAML 对象）。</description></item>
/// </list>
/// <para>
/// 行序固定为**自上而下**（提取时给 <c>GetDIBits</c> 传负高度），格式固定为 32bpp
/// <c>BI_RGB</c> —— 与 WinUI <c>WriteableBitmap</c> 的 Bgra8 布局逐字节一致，
/// 不需要任何行交换或通道重排。
/// </para>
/// </remarks>
public sealed class IconPixels
{
    /// <summary>构造图标像素。</summary>
    /// <param name="bgra">BGRA 像素数据，长度必须等于 <paramref name="width"/> × <paramref name="height"/> × 4。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    public IconPixels(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(width),
                actualValue: width,
                message: "图标尺寸必须为正。");
        }

        if (bgra.Length != width * height * 4)
        {
            throw new ArgumentException(
                $"像素数据长度 {bgra.Length} 与尺寸 {width}×{height}×4 不一致。",
                nameof(bgra));
        }

        Bgra = bgra;
        Width = width;
        Height = height;
    }

    /// <summary>BGRA 像素数据（premultiplied alpha）。</summary>
    public byte[] Bgra { get; }

    /// <summary>像素宽。</summary>
    public int Width { get; }

    /// <summary>像素高。</summary>
    public int Height { get; }
}
