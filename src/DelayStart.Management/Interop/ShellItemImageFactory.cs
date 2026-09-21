using System.Runtime.InteropServices;

namespace DelayStart.Management.Interop;

/// <summary>
/// Shell 图标提取的 COM 接口与 GDI 互操作声明（D30 / docs/design.md 7.5）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 用 <c>IShellItemImageFactory</c> 而不是 <c>SHGetFileInfo</c>：
/// 后者在高 DPI 下只有 16/32px 两种尺寸，64px 的列表行会糊（docs/design.md 7.5 与 D30 的定论，
/// 与 旧视觉规范不一致时**以此处为准**）。工厂按请求尺寸生成高分辨率图标。
/// </para>
/// <para>
/// <c>[ComImport]</c> 声明只能住 Management 层 —— NativeAOT 无 built-in COM（R12），
/// 搬进 Core 会被 <c>IsAotCompatible</c> 在构建期拦下（IL3052）。
/// </para>
/// </remarks>
[ComImport]
[Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    /// <summary>
    /// 取与请求尺寸最匹配的图标 / 缩略图，返回 GDI <c>HBITMAP</c>（32bpp，含 alpha）。
    /// </summary>
    /// <param name="size">请求的像素尺寸。</param>
    /// <param name="flags"><see cref="ShellItemImageFactoryFlags"/> 组合。</param>
    /// <param name="phbm">成功时收到的位图句柄；调用方负责 <c>DeleteObject</c>。</param>
    /// <remarks>
    /// 接口只有这一个方法，vtable 无错位风险。失败时返回失败的 HRESULT ——
    /// 本包装保留 <c>PreserveSig</c> 语义（out 句柄 + HRESULT），
    /// 让调用方决定"取不到图标"是不是值得中断的事（图标提取的答案是：不是）。
    /// </remarks>
    [PreserveSig]
    int GetImage(InteropSize size, int flags, out IntPtr phbm);
}

/// <summary>Win32 <c>SIZE</c> 结构（<c>cx</c>/<c>cy</c>）。</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct InteropSize(int Width, int Height);

/// <summary><c>IShellItemImageFactory.GetImage</c> 的行为标志（<c>SIIGBF</c>）。</summary>
internal static class ShellItemImageFactoryFlags
{
    /// <summary>请求的尺寸比原图大时也接受（避免把 48px 的源硬拉伸判定为失败）。</summary>
    public const int BiggerSizeOk = 0x00000001;

    /// <summary>只要图标，不要缩略图。文件类型的图标由 shell 关联给出。</summary>
    public const int IconOnly = 0x00000004;
}

/// <summary>
/// Shell 图标提取的原生入口。
/// </summary>
/// <remarks>
/// <para>
/// <c>SHCreateItemFromParsingName</c> 与 <c>CoCreateInstance</c>（<see cref="ComFactory"/>）不同：
/// 它直接按**解析名**创建 Shell item，路径、<c>.lnk</c> 快捷方式与
/// <c>shell:AppsFolder\&lt;AUMID&gt;</c>（UWP 条目）都是合法解析名 ——
/// 一个入口覆盖全部三种来源，不需要按来源分流。
/// </para>
/// <para>
/// 对 <c>.lnk</c>：<c>SIIGBF_ICONONLY</c> 会让 shell 自动解析到**目标**的图标，
/// 无需先解开快捷方式。
/// </para>
/// </remarks>
internal static partial class ShellItemImageFactoryNative
{
    /// <summary>
    /// 按解析名创建 Shell item 并取 <see cref="IShellItemImageFactory"/> 接口。
    /// </summary>
    /// <param name="parsingName">路径 / <c>.lnk</c> / <c>shell:AppsFolder\&lt;AUMID&gt;</c>。</param>
    /// <param name="bindContext">绑定上下文；本层不做进度 / 检查点，恒传 <see cref="IntPtr.Zero"/>。</param>
    /// <param name="riid">接口 IID。</param>
    /// <param name="ppv">收到的接口指针；失败时无意义。成功时调用方负责 <see cref="Marshal.Release"/>。</param>
    /// <returns>HRESULT；非 0 表示失败（路径不存在、格式不识别等）。刻意不做
    /// <c>MarshalAs(UnmanagedType.Error)</c> —— 那会把失败变成异常，
    /// 而调用方要的是"Try"语义：拿返回码自行决定要不要当回事。</returns>
    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName")]
    private static partial int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string parsingName,
        IntPtr bindContext,
        in Guid riid,
        out IntPtr ppv);

    /// <summary>
    /// 创建工厂实例；失败返回 <see langword="null"/>（调用方决定要不要报错）。
    /// </summary>
    /// <param name="parsingName">解析名。</param>
    /// <returns>工厂的 RCW；调用方负责 <see cref="Marshal.ReleaseComObject"/>。</returns>
    public static IShellItemImageFactory? TryCreateFactory(string parsingName)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        var hr = SHCreateItemFromParsingName(parsingName, IntPtr.Zero, in iid, out var pointer);

        if (hr != 0)
        {
            return null;
        }

        try
        {
            // 与 ComFactory.CreateInstance 相同的两步走：先取原始指针再建 RCW，
            // 避免 [ComImport] class 的 CS0030 问题（坑 3）。
            return (IShellItemImageFactory)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            _ = Marshal.Release(pointer);
        }
    }
}

/// <summary>
/// 把 <c>HBITMAP</c> 的像素读成 BGRA 字节数组所需的 GDI 互操作。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <c>Bitmap.FromHbitmap</c>（System.Drawing）**会丢 alpha 通道** —— 它假定输入是 24bpp。
/// 图标恰恰靠 alpha 做圆角与半透明边缘，所以必须用 <c>GetDIBits</c> 读 32bpp 原始数据。
/// </para>
/// <para>
/// <c>biHeight</c> 传**负值**请求自上而下的行序：DIB 默认自下而上，
/// 不翻转的话图标显示出来是倒立的。
/// </para>
/// </remarks>
internal static partial class GdiInterop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader bmiHeader;

        // 32bpp BI_RGB 不需要调色板，但结构里要占位让 marshal 尺寸正确。
        public uint bmiColors;
    }

    private const uint BiRgb = 0u;
    private const ushort Planes = 1;
    private const ushort BitsPerPixel = 32;
    private const int DibRgbColors = 0;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDC(IntPtr hWnd);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(IntPtr hWnd, IntPtr hDc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr hObject);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDIBits(
        IntPtr hDc,
        IntPtr hBitmap,
        uint startScanLine,
        uint scanLines,
        byte[]? bitmapBits,
        ref BitmapInfo bitmapInfo,
        uint usage);

    /// <summary>
    /// 读取 32bpp <c>HBITMAP</c> 的全部像素（BGRA，自上而下）。
    /// </summary>
    /// <param name="hBitmap">目标位图句柄。</param>
    /// <param name="width">像素宽。</param>
    /// <param name="height">像素高。</param>
    /// <returns>像素数据；读取失败为 <see langword="null"/>。</returns>
    public static byte[]? TryGetPixels(IntPtr hBitmap, int width, int height)
    {
        if (hBitmap == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return null;
        }

        var info = new BitmapInfo
        {
            bmiHeader = new BitmapInfoHeader
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BitmapInfoHeader>(),
                biWidth = width,
                biHeight = -height, // 负值 = 自上而下，免得图标倒立。
                biPlanes = Planes,
                biBitCount = BitsPerPixel,
                biCompression = BiRgb,
            },
        };

        var pixels = new byte[checked(width * height * 4)];
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var ok = GetDIBits(
                screenDc,
                hBitmap,
                startScanLine: 0,
                (uint)height,
                pixels,
                ref info,
                DibRgbColors);

            return ok ? pixels : null;
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>释放 <c>GetImage</c> 返回的 <c>HBITMAP</c>；句柄为 0 时是安全的空操作。</summary>
    /// <param name="hBitmap">位图句柄。</param>
    public static void DeleteBitmap(IntPtr hBitmap)
    {
        if (hBitmap != IntPtr.Zero)
        {
            _ = DeleteObject(hBitmap);
        }
    }
}
