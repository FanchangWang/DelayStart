using DelayStart.Management.Models;

namespace DelayStart.Management.Services;

/// <summary>
/// 系统关键启动位置（驱动 / Winlogon / 登录脚本）的只读检查（FR-7.3，D30 推后项）。
/// </summary>
/// <remarks>
/// 🔴 **本服务永远只读**：驱动不接管、Winlogon 的 Shell/Userinit 不显示可写控件、
/// 登录脚本不修改组策略。页面会明确标注"系统关键项，本软件不接管"。
/// </remarks>
public static class SystemStartupInspector
{
    private const string ServicesKeyPath = @"SYSTEM\CurrentControlSet\Services";
    private const string WinlogonKeyPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";

    /// <summary>读取 Winlogon 关键值（Shell / Userinit / VMApplet）—— 这三处被篡改是常见劫持点。</summary>
    /// <returns>只读条目列表。</returns>
    public static IReadOnlyList<ReadOnlyEntry> ReadWinlogon()
    {
        var entries = new List<ReadOnlyEntry>();

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(WinlogonKeyPath);
            if (key is not null)
            {
                foreach (var valueName in new[] { "Shell", "Userinit", "VMApplet", "AppSetup" })
                {
                    if (key.GetValue(valueName) is string value)
                    {
                        entries.Add(new ReadOnlyEntry($@"HKLM\{WinlogonKeyPath}", valueName, value));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            // 读不到（权限/被保护）时按"无条目"处理：该页本来就是补充展示。
        }

        return entries;
    }

    /// <summary>读取组策略登录脚本（机器侧 + 用户侧）。</summary>
    /// <returns>只读条目列表。</returns>
    public static IReadOnlyList<ReadOnlyEntry> ReadLogonScripts()
    {
        var entries = new List<ReadOnlyEntry>();

        foreach (var (root, label) in new (Microsoft.Win32.RegistryKey, string)[]
                 {
                     (Microsoft.Win32.Registry.LocalMachine, @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Logon"),
                     (Microsoft.Win32.Registry.CurrentUser, @"HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Logon"),
                 })
        {
            try
            {
                using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Group Policy\Scripts\Logon");
                if (key is null)
                {
                    continue;
                }

                foreach (var gid in key.GetSubKeyNames())
                {
                    using var scriptKey = key.OpenSubKey(gid);
                    if (scriptKey is null)
                    {
                        continue;
                    }

                    foreach (var scriptId in scriptKey.GetSubKeyNames())
                    {
                        using var item = scriptKey.OpenSubKey(scriptId);
                        var script = item?.GetValue("Script") as string;
                        if (!string.IsNullOrWhiteSpace(script))
                        {
                            entries.Add(new ReadOnlyEntry(label, System.IO.Path.GetFileName(script), script));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
            {
                // 同 ReadWinlogon：只读降级。
            }
        }

        return entries;
    }

    /// <summary>统计自启动位置里的内核驱动数量（只读展示用）。</summary>
    /// <returns>驱动数量。</returns>
    public static int CountKernelDrivers()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(ServicesKeyPath);
            if (key is null)
            {
                return 0;
            }

            var count = 0;
            foreach (var name in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(name);
                if (sub?.GetValue("Type") is int type
                    && (type & 0x3) != 0
                    && sub.GetValue("Start") is int start
                    && start is 0 or 1 or 2)
                {
                    count++;
                }
            }

            return count;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or System.IO.IOException)
        {
            return 0;
        }
    }
}
