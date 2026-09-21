using System.Runtime.InteropServices;

namespace DelayStart.Scheduler;

/// <summary>
/// <see cref="NativeMethods"/> 的清单资源读取面（D70）：从目标 exe 里读 RT_MANIFEST，
/// 判 <c>uiAccess</c> 声明 —— 只读资源，不执行任何目标代码。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 必须 <c>LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE</c>：
/// 只把 PE 当数据文件映射（不重定位、不执行 DllMain、不加载依赖），读资源零副作用。
/// </para>
/// </remarks>
internal static partial class NativeMethods
{
    /// <summary>RT_MANIFEST 资源类型 ID（winuser.h：CREATEPROCESS_MANIFEST_RESOURCE_ID = 1）。</summary>
    private static readonly nint RtManifestType = 24;

    /// <summary>主清单资源 ID；ISOLATIONAWARE 清单用 ID 2。</summary>
    private static readonly nint[] ManifestResourceIds = [1, 2];

    private const uint LoadLibraryAsDataFile = 0x0000_0002;
    private const uint LoadLibraryAsImageResource = 0x0000_0020;

    /// <summary>
    /// 读取目标 exe 的嵌入清单文本；读不到（无清单 / 非 PE / 文件被占）返回 <see langword="null"/>。
    /// </summary>
    internal static string? TryReadEmbeddedManifest(string exePath)
    {
        // SetErrorMode 无需：AS_DATAFILE 映射失败只是返回 0，不弹对话框（LoadLibraryEx 对
        // 数据文件映射不执行加载器逻辑，不触发"不是有效的 Win32 应用程序"弹窗）。
        var module = LoadLibraryExW(exePath, 0, LoadLibraryAsDataFile | LoadLibraryAsImageResource);
        if (module == 0)
        {
            return null;
        }

        try
        {
            foreach (var id in ManifestResourceIds)
            {
                var resourceInfo = FindResourceW(module, id, RtManifestType);
                if (resourceInfo == 0)
                {
                    continue;
                }

                var resourceData = LoadResource(module, resourceInfo);
                if (resourceData == 0)
                {
                    continue;
                }

                var pointer = LockResource(resourceData);
                var size = SizeofResource(module, resourceInfo);
                if (pointer == 0 || size == 0)
                {
                    continue;
                }

                unsafe
                {
                    // 清单通常 UTF-8（可带 BOM）；按 UTF-8 解码，BOM 由 GetString 处理为 U+FEFF，
                    // 对标签正则无影响。
                    return System.Text.Encoding.UTF8.GetString((byte*)pointer, (int)size);
                }
            }

            return null;
        }
        finally
        {
            _ = FreeLibrary(module);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint LoadLibraryExW(string fileName, nint fileHandle, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint FindResourceW(nint module, nint name, nint type);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint LoadResource(nint module, nint resourceInfo);

    [LibraryImport("kernel32.dll")]
    private static partial nint LockResource(nint resourceData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SizeofResource(nint module, nint resourceInfo);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeLibrary(nint module);
}
