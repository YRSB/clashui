using System.Diagnostics;

namespace Clashui.Core;

/// 开机自启：注册计划任务（ONLOGON + 最高权限），这样提权运行不会在每次登录时弹 UAC。
public static class AutoStart
{
    private const string TaskName = "Clashui";

    public static bool IsRegistered()
    {
        return Run("schtasks", $"/Query /TN {TaskName}") == 0;
    }

    /// 需要在管理员权限下调用（RL HIGHEST 要求）。
    public static bool Register(string exePath)
    {
        return Run("schtasks", $"/Create /F /TN {TaskName} /SC ONLOGON /RL HIGHEST /TR \"\\\"{exePath}\\\"\"") == 0;
    }

    public static bool Unregister()
    {
        return Run("schtasks", $"/Delete /F /TN {TaskName}") == 0;
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
