using System.Runtime.InteropServices;
using System.Text;

using DelayStart.Core.Abstractions;
using DelayStart.Core.Launch;
using DelayStart.Core.Services;
using DelayStart.Management.Interop;

using Microsoft.Win32;

namespace DelayStart.Management.Services;

/// <summary>
/// 系统侧身份注册（D79）：开始菜单快捷方式的 AUMID + <c>delaystart:</c> 协议处理器。
/// </summary>
/// <remarks>
/// <para>
/// 🔴 <b>为什么未打包应用发通知还需要这一步</b>：<c>ToastNotificationManager</c> 认的不是
/// 进程，是一个叫 <b>AUMID</b> 的字符串，而这个字符串必须先在系统里"存在" ——
/// 对未打包应用而言，存在的方式就是**开始菜单里有一个把它写进
/// <c>System.AppUserModel.ID</c> 属性的快捷方式**。快捷方式同时提供通知上显示的名字与图标，
/// 所以"通知标题显示 DelayStart"这件事也由它决定（<see cref="AppUserModelId"/> 本身不会显示给用户）。
/// </para>
/// <para>
/// 🔴 <b>点击通知为什么会拉起管理端</b>：通知 XML 里用
/// <c>activationType="protocol"</c> + <c>launch="delaystart://…"</c>，点击即由 Shell 启动协议处理器。
/// 这条路刻意**不用 COM 通知激活服务器**（<c>INotificationActivationCallback</c>）——
/// 那需要在注册表登记一个 CLSID 并让管理端实现一个进程内类工厂，代价远大于收益，
/// 而且发通知的守卫进程早就退出了，本来也没有活着的回调对象可指。
/// </para>
/// <para>
/// 两侧都会调用（管理端每次启动、守卫发通知前），因此**必须幂等且绝不能抛异常**：
/// 注册失败只降级成"这次通知弹不出来"，绝不能让管理端起不来或让守卫巡检失败。
/// </para>
/// </remarks>
public sealed class ShellRegistrationService
{
    /// <summary>本产品的应用用户模型 ID（AUMID）。跨进程契约，不要改。</summary>
    /// <remarks>
    /// 值收拢在 <c>DelayStart.Core.Launch.NotifyToastJob.DefaultAumid</c>（Core）：
    /// 调度端与通知中转器不能引用 Management，但又必须写同一个 AUMID。
    /// </remarks>
    public const string AppUserModelId = DelayStart.Core.Launch.NotifyToastJob.DefaultAumid;

    /// <summary>开始菜单快捷方式文件名（与安装器 <c>[Icons]</c> 同名同路径）。</summary>
    public const string ShortcutFileName = "DelayStart.lnk";

    private const int MaxPath = 1024;

    private readonly PathService _paths;
    private readonly ILogSink _log;

    /// <summary>构造注册服务。</summary>
    /// <param name="paths">路径服务，取管理端 exe 与安装根。</param>
    /// <param name="log">日志接收端。</param>
    public ShellRegistrationService(PathService paths, ILogSink log)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(log);

        _paths = paths;
        _log = log;
    }

    /// <summary>
    /// 开始菜单快捷方式路径。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>{autoprograms}</c> 与安装器用的是同一个目录（当前用户的开始菜单），
    /// 所以这里写的就是安装器建的那一个 —— <b>有意覆盖而不是另建一份</b>：
    /// 两份同名快捷方式只会让"通知里显示的图标"变成一个竞态问题。
    /// </para>
    /// <para>
    /// <c>static</c>：路径只由当前用户与常量决定，与实例状态无关
    /// （安装器与诊断脚本也按 <c>ShellRegistrationService.ShortcutPath</c> 取它）。
    /// </para>
    /// </remarks>
    public static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs),
        ShortcutFileName);

    /// <summary>协议处理器注册表路径（相对 <c>HKCU</c>）。</summary>
    public static string ProtocolRegistryPath =>
        $@"Software\Classes\{AppActivation.ProtocolScheme}";

    /// <summary>
    /// 确保身份注册齐备（快捷方式 AUMID + 协议处理器）。
    /// </summary>
    /// <returns>两项都就绪时为 <see langword="true"/>；任一失败为 <see langword="false"/>（原因已进日志）。</returns>
    public bool EnsureRegistered()
    {
        // 两项互相独立：协议没注册好不影响通知本身能弹出来（只是点不开），
        // 所以分开 try，别让一个失败连带跳过另一个。
        var shortcut = TryRegisterShortcut();
        var protocol = TryRegisterProtocol();
        return shortcut && protocol;
    }

    private bool TryRegisterShortcut()
    {
        try
        {
            if (!NeedsShortcutWrite())
            {
                return true;
            }

            WriteShortcut();
            _log.Info($"已注册开始菜单快捷方式（AUMID={AppUserModelId}）：{ShortcutPath}");
            return true;
        }
        catch (Exception ex)
        {
            // 不是 COMException 也吞：这里已经在"降级"路径上了，把异常抛给调用方
            // 只会让守卫巡检失败或管理端起不来，代价远大于"通知弹不出来"。
            _log.Warn(ex, $"注册开始菜单快捷方式失败，系统通知可能无法显示：{ShortcutPath}");
            return false;
        }
    }

    private bool TryRegisterProtocol()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(ProtocolRegistryPath, writable: true);
            if (key is null)
            {
                _log.Warn($"无法创建协议注册表键：HKCU\\{ProtocolRegistryPath}");
                return false;
            }

            var command = $"\"{_paths.ManagerExecutablePath}\" {AppActivation.GotoStartupArgument} \"%1\"";
            var existing = key.OpenSubKey(@"shell\open\command")?.GetValue(null) as string;
            if (string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // 「URL Protocol」的值必须是空串（存在即可，内容不重要）——
            // 这是 Windows 区分"协议处理器"与"普通文件类型"的判据。
            key.SetValue(null, "URL:DelayStart 定位协议");
            key.SetValue("URL Protocol", string.Empty);

            using var commandKey = key.CreateSubKey(@"shell\open\command", writable: true);
            if (commandKey is null)
            {
                _log.Warn("无法创建协议命令注册表键");
                return false;
            }

            commandKey.SetValue(null, command);
            _log.Info($"已注册协议处理器：{AppActivation.ProtocolScheme}:// → {command}");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warn(ex, $"注册协议处理器失败，点击通知将无法拉起管理端：{AppActivation.ProtocolScheme}");
            return false;
        }
    }

    /// <summary>判断快捷方式是否需要重写（缺失 / 目标变了 / AUMID 丢了）。</summary>
    /// <returns>需要写时为 <see langword="true"/>。</returns>
    /// <remarks>
    /// 加这道判断是为了不每次启动都重写一遍用户的开始菜单项 ——
    /// 重写本身无害，但会让快捷方式的修改时间在每次启动时抖动，排查问题时全是噪声。
    /// 读失败一律按"需要重写"处理：重写是幂等的，猜错方向的代价不对称。
    /// </remarks>
    private bool NeedsShortcutWrite()
    {
        if (!File.Exists(ShortcutPath))
        {
            return true;
        }

        try
        {
            var link = ComFactory.CreateInstance<IShellLinkW>(ShellLinkClassId.Value);
            try
            {
                ((IPersistFile)link).Load(ShortcutPath, ShellLinkFlags.StgmRead);

                var pathBuffer = new StringBuilder(MaxPath);
                link.GetPath(pathBuffer, pathBuffer.Capacity, IntPtr.Zero, ShellLinkFlags.RawPath | ShellLinkFlags.NoUi);
                if (!string.Equals(
                        pathBuffer.ToString().Trim(),
                        _paths.ManagerExecutablePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                var key = new PropertyKey(ShellPropertyKeys.AppUserModelFormatId, ShellPropertyKeys.AppUserModelIdPid);
                ((IPropertyStore)link).GetValue(ref key, out var value);
                try
                {
                    return value.VarType != PropVariantInterop.VtLpwstr
                        || !string.Equals(
                            Marshal.PtrToStringUni(value.PointerValue),
                            AppUserModelId,
                            StringComparison.Ordinal);
                }
                finally
                {
                    PropVariantInterop.Clear(ref value);
                }
            }
            finally
            {
                _ = Marshal.ReleaseComObject(link);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or InvalidCastException
            or FileNotFoundException or ArgumentException or UnauthorizedAccessException)
        {
            _log.Warn(ex, $"读取现有快捷方式失败，将重写：{ShortcutPath}");
            return true;
        }
    }

    /// <summary>写出（或覆盖）带 AUMID 的开始菜单快捷方式。</summary>
    private void WriteShortcut()
    {
        var link = ComFactory.CreateInstance<IShellLinkW>(ShellLinkClassId.Value);
        try
        {
            link.SetPath(_paths.ManagerExecutablePath);
            link.SetWorkingDirectory(_paths.InstalledRoot);
            link.SetDescription("DelayStart 开机自启动错峰管理器");
            link.SetIconLocation(_paths.ManagerExecutablePath, 0);

            // AUMID 只住在属性存储里 —— IShellLink 的字段里没有它。
            var key = new PropertyKey(ShellPropertyKeys.AppUserModelFormatId, ShellPropertyKeys.AppUserModelIdPid);
            var value = PropVariantInterop.FromString(AppUserModelId);
            try
            {
                var store = (IPropertyStore)link;
                store.SetValue(ref key, ref value);
                store.Commit();
            }
            finally
            {
                PropVariantInterop.Clear(ref value);
            }

            var folder = Path.GetDirectoryName(ShortcutPath);
            if (!string.IsNullOrEmpty(folder))
            {
                _ = Directory.CreateDirectory(folder);
            }

            // fRemember=true：把"保存到哪个文件"记住，否则某些 Shell 实现会写进临时路径。
            ((IPersistFile)link).Save(ShortcutPath, fRemember: true);
        }
        finally
        {
            _ = Marshal.ReleaseComObject(link);
        }
    }
}
