using System.Diagnostics;

namespace ClashUI.Core;

public static class AutoStart
{
    private const string TaskName = "ClashUI";

    public static bool IsRegistered()
    {
        return Run("schtasks", $"/Query /TN {TaskName}") == 0;
    }

    public static bool Register(string exePath)
    {
        var ok = Run("schtasks", $"/Create /F /TN {TaskName} /SC ONLOGON /RL HIGHEST /TR \"\\\"{exePath}\\\" --silent\"") == 0;
        if (ok) EnsureTaskSettings();
        return ok;
    }

    public static void EnsureTaskSettings()
    {
        Run("powershell", "-NoProfile -NonInteractive -Command \"Set-ScheduledTask -TaskName ClashUI -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero))\"");
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
                WorkingDirectory = Environment.SystemDirectory,
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
