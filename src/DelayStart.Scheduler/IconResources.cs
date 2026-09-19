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

    /// <summary>解析 ICO 容器并生成 HICON。取容器内第一个条目（本项目的图标资源只含一个尺寸）。</summary>
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

        // ICONDIRENTRY 从偏移 6 开始，每条 16 字节；取第一个条目的数据段。
        var dataOffset = ReadUInt32(icoBytes, 6 + 12);
        var dataLength = ReadUInt32(icoBytes, 6 + 8);
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
                0,
                0,
                NativeMethods.LrDefaultsize);
        }
    }

    private static ushort ReadUInt16(byte[] data, int offset)
        => (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadUInt32(byte[] data, int offset)
        => (uint)(data[offset]
            | (data[offset + 1] << 8)
            | (data[offset + 2] << 16)
            | (data[offset + 3] << 24));
}
