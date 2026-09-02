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
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
            });
            return true;
        }
        catch
        {
            return false; // 用户取消了 UAC
        }
    }
}
