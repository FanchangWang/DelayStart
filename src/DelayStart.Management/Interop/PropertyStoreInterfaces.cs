using System.Runtime.InteropServices;

namespace DelayStart.Management.Interop;

/// <summary>
/// <c>PROPERTYKEY</c>：属性存储里的键（<c>FMTID</c> + <c>PID</c>）。
/// </summary>
/// <remarks>
/// 与 <c>GUID</c> 一样是**内存布局契约**：<c>GUID(16) + DWORD(4) = 20 字节</c>。
/// 显式写 <c>Pack = 4</c> 是为了让"4 字节对齐"这件事在代码里可见 ——
/// 见 <c>docs/pitfalls.md</c> 十一：结构体对齐错了，调用会以 <c>E_INVALIDARG</c> 静默失败。
/// </remarks>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct PropertyKey
{
    /// <summary>属性集的格式标识。</summary>
    public Guid FormatId;

    /// <summary>属性在属性集内的序号。</summary>
    public uint PropertyId;

    /// <summary>构造一个键。</summary>
    /// <param name="formatId">格式标识。</param>
    /// <param name="propertyId">属性序号。</param>
    public PropertyKey(Guid formatId, uint propertyId)
    {
        FormatId = formatId;
        PropertyId = propertyId;
    }
}

/// <summary>
/// <c>PROPVARIANT</c> 的最小声明：只承载 <c>VT_LPWSTR</c>。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 用显式布局而不是 <c>Sequential</c>：真实的 <c>PROPVARIANT</c> 是
/// <c>USHORT vt; WORD wReserved1..3; union { ... }</c> —— 联合体在 x64 上从**偏移 8** 开始
/// （前面 8 字节是类型与保留位）。按 <c>Sequential</c> 顺序摆两个字段只会得到 12 字节，
/// 后面 4 字节是垃圾，<c>SetValue</c> 会直接失败。
/// </para>
/// <para>
/// 只支持字符串是刻意的：本层唯一的用途是把 AUMID 写进快捷方式属性，
/// 多声明一个联合体成员就多一处对齐陷阱。
/// </para>
/// </remarks>
[StructLayout(LayoutKind.Explicit, Size = 16)]
internal struct PropVariant
{
    /// <summary>类型标签，<c>VT_LPWSTR</c> = 31。</summary>
    [FieldOffset(0)]
    public ushort VarType;

    /// <summary>字符串指针（<c>CoTaskMemAlloc</c> 分配，用完必须 <c>PropVariantClear</c>）。</summary>
    [FieldOffset(8)]
    public nint PointerValue;
}

/// <summary>
/// <c>IPropertyStore</c>：读写 Shell 对象的属性（快捷方式的 AUMID 就住在这里）。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 为什么必须走属性存储而不是"给 lnk 文件加个标记"：未打包应用要发系统通知，
/// 唯一的身份来源是**开始菜单快捷方式上的 <c>System.AppUserModel.ID</c>**。
/// 它不在 <c>IShellLink</c> 的字段里，只在属性存储里。
/// </para>
/// <para>
/// vtable 顺序不能改（同 <see cref="IShellLinkW"/>）：<c>GetCount → GetAt → GetValue →
/// SetValue → Commit</c>。中间少一个槽位，后面所有调用都会错位。
/// </para>
/// </remarks>
[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    /// <summary>属性个数。</summary>
    /// <param name="cProps">接收个数。</param>
    void GetCount(out uint cProps);

    /// <summary>按下标取属性键。</summary>
    /// <param name="iProp">下标。</param>
    /// <param name="pkey">接收属性键。</param>
    void GetAt(uint iProp, out PropertyKey pkey);

    /// <summary>读一个属性。</summary>
    /// <param name="key">属性键。</param>
    /// <param name="pv">接收值（读字符串时由调用方负责 <c>PropVariantClear</c>）。</param>
    void GetValue(ref PropertyKey key, out PropVariant pv);

    /// <summary>写一个属性（写完必须 <see cref="Commit"/> 才生效）。</summary>
    /// <param name="key">属性键。</param>
    /// <param name="pv">要写入的值。</param>
    void SetValue(ref PropertyKey key, ref PropVariant pv);

    /// <summary>提交本批量修改。</summary>
    void Commit();
}

/// <summary>本层用到的属性键。</summary>
internal static class ShellPropertyKeys
{
    /// <summary><c>PKEY_AppUserModel_ID</c> 的格式标识 <c>{9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3}</c>。</summary>
    public static readonly Guid AppUserModelFormatId = new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3");

    /// <summary><c>PKEY_AppUserModel_ID</c> 的属性序号（5）。</summary>
    public const uint AppUserModelIdPid = 5;
}

/// <summary><c>PROPVARIANT</c> 的释放与构造。</summary>
internal static partial class PropVariantInterop
{
    /// <summary><c>VT_LPWSTR</c>：以 <c>L'\0'</c> 结尾的宽字符串指针。</summary>
    public const ushort VtLpwstr = 31;

    /// <summary>
    /// 用托管字符串造一个 <c>VT_LPWSTR</c> 变体。
    /// </summary>
    /// <param name="value">要承载的字符串。</param>
    /// <returns>变体；调用方必须在使用后 <see cref="Clear"/>，否则字符串内存泄漏。</returns>
    public static PropVariant FromString(string value) => new()
    {
        VarType = VtLpwstr,
        PointerValue = Marshal.StringToCoTaskMemUni(value),
    };

    /// <summary>释放变体内部的原生内存（<c>PropVariantClear</c>）。</summary>
    /// <param name="variant">要清空的变体。</param>
    public static void Clear(ref PropVariant variant) => _ = PropVariantClear(ref variant);

    [LibraryImport("ole32.dll", EntryPoint = "PropVariantClear")]
    private static partial int PropVariantClear(ref PropVariant pvar);
}
