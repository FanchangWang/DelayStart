using System.Runtime.InteropServices;

namespace DelayStart.Management.Interop;

/// <summary>
/// 服务 / 驱动注册表 MUI 字符串的**完整解析管道**（2026-09-20 批复 1）。
/// </summary>
/// <remarks>
/// <para>
/// 注册表里的显示名 / 描述形如 <c>@%SystemRoot%\System32\aaa.dll,-100</c>。
/// <c>SHLoadIndirectString</c> 只覆盖最常见的两种（路径仅含 <c>%SystemRoot%</c> 的
/// <c>@dll,-id</c>、<c>@{包?资源}</c>），真机实测还有两类漏网：
/// </para>
/// <list type="number">
/// <item><description>路径含其它环境变量（<c>%ProgramFiles%</c> 等）—— SH 解析失败，残留 <c>@%</c> 开头原文；</description></item>
/// <item><description>INF 格式 <c>@xxx.inf,%标识%;回退文本</c>（大量内置驱动）。</description></item>
/// </list>
/// <para>
/// 管道：SH 直解 → 环境变量展开后重试 → <c>LoadLibraryEx</c>（数据文件）+ <c>LoadString</c>
/// 直读资源（用户提供过的 Get-MuiString PowerShell 样例同款）→ INF 分号回退 → 空串（UI 回退键名）。
/// </para>
/// </remarks>
internal static partial class MuiString
{
    private const uint LoadLibraryAsDataFile = 0x00000002;
    private const uint LoadLibraryAsImageResource = 0x00000020;

/// <summary>把注册表 MUI 间接字符串解析成可读文本；<c>@</c> 开头且全部手段失败时返回空串（调用方回退服务键名）。</summary>
/// <remarks>
/// 2026-09-20 批复 1（Round H）：用 shell.csv（247 条解析失败）实测四级管道 ——
/// 226 条可救回，剩 21 条（IsoSessionServer / UCPD / pvhdparser 等）字符串外置到
/// 不存在的 .mui 或 DriverStore 包内，真机上没有可读资源 → 返回空串让 UI 回退键名，
/// 绝不再把 <c>@%</c> 原文显示给用户。
/// </remarks>
    public static string Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw[0] != '@')
        {
            return raw ?? string.Empty;
        }

        // ① SH 直解（%SystemRoot% 形式、@{包?资源} 形式）。
        if (Shlwapi.TryLoadIndirectString(raw, out var text))
        {
            return text;
        }

        // ② 展开环境变量后重试：SH 只保证认 %SystemRoot%，%ProgramFiles% 等认不得。
        var expanded = Environment.ExpandEnvironmentVariables(raw);
        if (!string.Equals(expanded, raw, StringComparison.Ordinal)
            && Shlwapi.TryLoadIndirectString(expanded, out text))
        {
            return text;
        }

        // ③ @路径,-编号 → LoadLibraryEx(数据文件) + LoadString 直读。
        var (modulePath, resourceId) = SplitIndirectReference(expanded);
        if (modulePath is not null && resourceId is not null && TryLoadString(modulePath, resourceId.Value, out text))
        {
            return text;
        }

        // ④ INF 格式 "@x.inf,%标识%;回退文本" —— 最后分号后就是已本地化的回退名。
        var separator = raw.LastIndexOf(';');
        if (separator >= 0 && separator + 1 < raw.Length)
        {
            var fallback = raw[(separator + 1)..].Trim();
            if (fallback.Length > 0 && fallback[0] != '@')
            {
                return fallback;
            }
        }

        // ⑤ 全部失败：返回空串而不是原文 —— 调用方（ServiceQueryService）对空显示名回退
        //    服务键名（如 "UCPD"），对空描述直接不显示；都比残留一串 @% 原文可读。
        //    （真机实测 shell.csv：247 条解析失败里 226 条可被①-④救回，剩 21 条的字符串
        //    外置到不存在的 .mui / DriverStore，无可读资源可取。）
        return string.Empty;
    }

    /// <summary>把 <c>@路径,-编号</c> 拆成路径与资源编号；不是该格式返回空对。</summary>
    private static (string? Path, uint? Id) SplitIndirectReference(string source)
    {
        var marker = source.LastIndexOf(",-", StringComparison.Ordinal);
        if (marker < 2 || marker + 2 >= source.Length)
        {
            return (null, null);
        }

        var path = source[1..marker].Trim().Trim('"');
        if (path.Length == 0 || !uint.TryParse(source[(marker + 2)..].TrimEnd(')'), out var id))
        {
            return (null, null);
        }

        return (path, id);
    }

    /// <summary>以数据文件方式载入模块并直读字符串资源；不执行 DllMain、失败不抛异常。</summary>
    private static bool TryLoadString(string modulePath, uint resourceId, out string text)
    {
        text = string.Empty;
        nint module = 0;
        try
        {
            module = LoadLibraryExW(modulePath, 0, LoadLibraryAsDataFile | LoadLibraryAsImageResource);
            if (module == 0)
            {
                return false;
            }

            var buffer = new char[512];
            var length = LoadStringW(module, resourceId, buffer, buffer.Length);
            if (length <= 0)
            {
                return false;
            }

            text = new string(buffer, 0, length);
            return text.Trim().Length > 0;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (module != 0)
            {
                _ = FreeLibrary(module);
            }
        }
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryExW(string fileName, nint fileHandle, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "LoadStringW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial int LoadStringW(nint instance, uint id, [Out] char[] buffer, int bufferMax);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);
}
