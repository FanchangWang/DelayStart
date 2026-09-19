using System.Runtime.InteropServices;

namespace DelayStart.Management.Interop;

/// <summary>
/// COM 组件实例化的最小封装（<c>CoCreateInstance</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 本层用 COM 是安全的（Management 不参与 NativeAOT），但**绝不能**把同样的代码搬进 Core ——
/// NativeAOT 没有 built-in COM，<c>Marshal.GetObjectForIUnknown</c> 会抛
/// <c>PlatformNotSupportedException</c>（R12）。Core 开了 <c>IsAotCompatible=true</c>，
/// 搬过去会在构建期被 IL3052 拦下。
/// </para>
/// <para>
/// 不用 <c>[ComImport] class</c> + <c>new</c> 的经典写法：那样拿不到编译期认可的转换（CS0030）。
/// 这里先把接口指针取出来，再由 <see cref="Marshal.GetObjectForIUnknown"/> 建 RCW ——
/// <c>object</c> → 接口的显式转换是合法引用转换，运行期由 RCW 完成 QueryInterface。
/// </para>
/// </remarks>
internal static partial class ComFactory
{
    /// <summary>进程内服务器（<c>CLSCTX_INPROC_SERVER</c>）。Shell 组件都是进程内组件。</summary>
    private const uint ClsCtxInprocServer = 0x1;

    /// <summary>
    /// 创建指定 CLSID 的 COM 对象并转换为接口 <typeparamref name="T"/>。
    /// </summary>
    /// <typeparam name="T">目标接口类型，必须带 <c>[Guid]</c>（即 <c>IID</c>）。</typeparam>
    /// <param name="clsid">组件类标识。</param>
    /// <returns>接口的 RCW。调用方负责在结束时 <see cref="Marshal.ReleaseComObject"/>。</returns>
    /// <exception cref="COMException">创建失败时抛出。</exception>
    public static T CreateInstance<T>(Guid clsid)
        where T : class
    {
        var iid = typeof(T).GUID;
        var hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsCtxInprocServer, ref iid, out var pointer);

        if (hr != 0)
        {
            // 刻意不抛 COMException：它属于"运行时保留"的异常类型（CA2201），
            // 由运行时自己抛出才合适。这里表达的是应用层事实"组件创建失败"，
            // 把 HRESULT 写进消息同样能定位问题。
            throw new InvalidOperationException($"创建 COM 对象失败（CLSID {clsid:B}）：HRESULT 0x{hr:X8}");
        }

        try
        {
            // GetObjectForIUnknown 会让 COM 层做一次 QueryInterface 到实际类型，
            // 所以这里的强制转换在运行期是安全的。
            return (T)Marshal.GetObjectForIUnknown(pointer);
        }
        finally
        {
            // GetObjectForIUnknown 会自己 AddRef，所以这里的原始指针必须释放，
            // 否则每个快捷方式都会漏一个引用计数。
            _ = Marshal.Release(pointer);
        }
    }

    [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
    private static partial int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);
}
