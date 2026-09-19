using System.Security.Cryptography.X509Certificates;

using System.Runtime.InteropServices;

namespace DelayStart.Management.Interop;

/// <summary>
/// 读取 PE 文件内嵌 Authenticode 签名的**签名者证书主题**（只取证书，不验证证书链）。
/// </summary>
/// <remarks>
/// <para>
/// 2026-09-20 批复 1：服务 / 驱动的"Windows 内置"判据从路径改签名 ——
/// 路径判据会把装进 <c>system32</c> 的第三方服务 / 驱动全部误判为内置（默认视图隐藏）。
/// </para>
/// <para>
/// 实现走 crypt32 的 <c>CryptQueryObject</c>（内容类型 = PKCS7_SIGNED_EMBED，
/// 即 Authenticode 内嵌签名）：拿到签名消息内的证书库后，**签名者 = 不给库里任何
/// 证书签发的证书**（中间证书 / 根证书都是"签发别人"的；自签名根因 Subject=Issuer
/// 而自我排除）。直接取"库里含 Microsoft"是不行的 —— WHQL 代签的第三方驱动，其中间
/// 证书同样是微软 CA，会把 NVIDIA / Intel 误判成内置。
/// </para>
/// </remarks>
internal static partial class Authenticode
{
    private const uint CertQueryObjectFile = 1;
    // 🔴 真机实测：只给 PKCS7_SIGNED_EMBED（0x100）会报 CRYPT_E_NO_MATCH，ALL（0x3FFE）才行。
    private const uint CertQueryContentFlagAll = 0x00003FFE; // CERT_QUERY_CONTENT_FLAG_ALL
    private const uint CertQueryFormatFlagAll = 0x0000000E; // CERT_QUERY_FORMAT_FLAG_ALL

    /// <summary>取签名者证书主题（如 <c>CN=Microsoft Windows, O=Microsoft Corporation…</c>）。</summary>
    /// <param name="filePath">PE 文件路径。</param>
    /// <returns>签名者主题；无内嵌签名 / 文件读不了时为 <see langword="null"/>。</returns>
    public static string? TryGetSignerSubject(string filePath)
    {
        nint store = 0;
        nint message = 0;
        try
        {
            var opened = CryptQueryObject(
                CertQueryObjectFile,
                filePath,
                CertQueryContentFlagAll,
                CertQueryFormatFlagAll,
                0,
                out _,
                out _,
                out _,
                out store,
                out message,
                0);
            if (!opened || store == 0)
            {
                return null;
            }

            return FindSignerSubject(store);
        }
        catch (Exception ex) when (
            ex is IOException
                or ArgumentException
                or System.Security.SecurityException
                or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (message != 0)
            {
                _ = CryptMsgClose(message);
            }

            if (store != 0)
            {
                _ = CertCloseStore(store, 0);
            }
        }
    }

    /// <summary>在签名消息的证书库里找签名者（不签发任何其它证书的那张）。</summary>
    private static string? FindSignerSubject(nint store)
    {
        var certificates = new List<(string Subject, string Issuer)>();

        nint context = 0;
        nint next;
        while ((next = CertEnumCertificatesInStore(store, context)) != 0)
        {
            context = next;
            using var certificate = new X509Certificate2(context);
            certificates.Add((certificate.Subject, certificate.Issuer));
        }

        if (certificates.Count == 0)
        {
            return null;
        }

        // 签发者判定（含自身 —— 自签名根 Subject == Issuer，会被自我排除出候选）。
        foreach (var candidate in certificates)
        {
            var issuesAnyone = certificates.Any(other => other.Issuer == candidate.Subject);
            if (!issuesAnyone)
            {
                return candidate.Subject;
            }
        }

        // 全部互相签发（理论不达）→ 退回第一张。
        return certificates[0].Subject;
    }

    [LibraryImport("crypt32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptQueryObject(
        uint objectType,
        string objectPath,
        uint expectedContentTypeFlags,
        uint expectedFormatTypeFlags,
        uint flags,
        out uint msgAndCertEncodingType,
        out uint contentType,
        out uint formatType,
        out nint certStore,
        out nint message,
        nint reservedContext);

    /// <summary>枚举证书库；<paramref name="previousContext"/> 会被 API 释放，无需手动 CertFree。</summary>
    [LibraryImport("crypt32.dll")]
    private static partial nint CertEnumCertificatesInStore(nint store, nint previousContext);

    [LibraryImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptMsgClose(nint message);

    [LibraryImport("crypt32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CertCloseStore(nint store, uint flags);
}
