using System.Runtime.InteropServices;

namespace DelayStart.Management.Interop;

/// <summary>
/// <c>shlwapi.dll</c> 的最小封装。目前只用到 <c>SHLoadIndirectString</c>（FR-1.8）。
/// </summary>
/// <remarks>
/// <para>
/// UWP 应用的显示名在注册表里存的是**间接字符串引用**，形如
/// <c>@{Microsoft.WindowsCalculator_11.0.0.0_x64__8wekyb3d8bbwe?ms-resource://...}</c>，
/// 直接显示给用户没有意义，必须交给 <c>SHLoadIndirectString</c> 解析成「计算器」这样的可读文本。
/// </para>
/// <para>
/// 用 <see cref="LibraryImportAttribute"/> 而不是 <see cref="DllImportAttribute"/>：
/// 后者在 <c>AnalysisLevel=latest-recommended</c> 下会触发 <c>SYSLIB1054</c>，
/// 而本仓库 <c>TreatWarningsAsErrors=true</c> —— 那条建议在这里是硬要求。
/// </para>
/// </remarks>
internal static partial class Shlwapi
{
    private const string LibraryName = "shlwapi.dll";

    /// <summary>间接字符串解析后的最大长度（字符）。超出会被截断，属于可接受的降级。</summary>
    private const int BufferLength = 512;

    /// <summary>
    /// 尝试解析间接字符串。失败时返回 <see langword="false"/>，**不抛异常**。
    /// </summary>
    /// <param name="source">原始间接字符串，通常以 <c>@</c> 开头。</param>
    /// <param name="result">解析后的可读文本。</param>
    /// <returns>解析成功时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 调用方**必须**准备好失败回退（FR-1.8 的第 4 步）：本机可能缺少资源包、
    /// 语言区域不匹配、或该应用已被卸载而只剩注册表残留，这些情况下解析必然失败。
    /// </remarks>
    public static bool TryLoadIndirectString(string source, out string result)
    {
        result = string.Empty;

        if (string.IsNullOrWhiteSpace(source) || source[0] != '@')
        {
            // 不是间接引用，本身就是可读文本。
            result = source;
            return !string.IsNullOrWhiteSpace(source);
        }

        var buffer = new char[BufferLength];
        var hr = SHLoadIndirectString(source, buffer, buffer.Length, IntPtr.Zero);
        if (hr != 0)
        {
            return false;
        }

        // 封送回来的是以 NUL 结尾的缓冲，截到第一个 NUL。
        var end = Array.IndexOf(buffer, '\0');
        var text = new string(buffer, 0, end < 0 ? buffer.Length : end);

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        result = text;
        return true;
    }

    [LibraryImport(LibraryName, EntryPoint = "SHLoadIndirectString", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHLoadIndirectString(
        string pszSource,
        [Out] char[] pszOutBuf,
        int cchOutBuf,
        IntPtr ppvReserved);
}
