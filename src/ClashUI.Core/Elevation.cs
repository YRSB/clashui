using System.Diagnostics;
using System.Security.Principal;

namespace ClashUI.Core;

public static class Elevation
{
    public static bool IsElevated { get; } = Check();

    private static bool Check()
    {
        try
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// 弹 UAC 重启自身，返回是否成功发起了新进程。
    public static bool RelaunchElevated(string arguments = "")
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(arguments)) parts.Add(arguments.Trim());
        if (!parts.Any(p => p.Contains("--relaunch", StringComparison.Ordinal)))
            parts.Add("--relaunch");
        if (Environment.GetCommandLineArgs().Any(a => a is "--silent" or "-s")
            && !parts.Any(p => p.Contains("--silent", StringComparison.Ordinal) || p.Contains("-s", StringComparison.Ordinal)))
        {
            parts.Add("--silent");
        }
        var args = string.Join(" ", parts);
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = args,
            });
            return true;
        }
        catch
        {
            return false; // 用户取消了 UAC
        }
    }
}
