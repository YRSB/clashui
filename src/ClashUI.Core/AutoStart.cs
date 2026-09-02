using System.Diagnostics;

namespace ClashUI.Core;

/// 开机自启：注册计划任务（ONLOGON + 最高权限），这样提权运行不会在每次登录时弹 UAC。
public static class AutoStart
{
    private const string TaskName = "ClashUI";
    private const string LegacyTaskName = "Clashui";

    public static bool IsRegistered()
    {
        return Run("schtasks", $"/Query /TN {TaskName}") == 0 || Run("schtasks", $"/Query /TN {LegacyTaskName}") == 0;
    }

    /// 需要在管理员权限下调用（RL HIGHEST 要求）。注册的计划任务带 --silent，登录后静默启动。
    public static bool Register(string exePath)
    {
        var ok = Run("schtasks", $"/Create /F /TN {TaskName} /SC ONLOGON /RL HIGHEST /TR \"\\\"{exePath}\\\" --silent\"") == 0;
        if (ok) Run("schtasks", $"/Delete /F /TN {LegacyTaskName}");
        return ok;
    }

    public static bool Unregister()
    {
        var a = Run("schtasks", $"/Delete /F /TN {TaskName}") == 0;
        var b = Run("schtasks", $"/Delete /F /TN {LegacyTaskName}") == 0;
        return a || b;
    }

    private static int Run(string fileName, string arguments)
    {
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            });
            proc!.WaitForExit(15000);
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            AppLog.Error($"执行 {fileName} 失败", ex);
            return -1;
        }
    }
}
