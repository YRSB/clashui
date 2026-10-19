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
        var tmpXml = Path.Combine(Path.GetTempPath(), $"clashui-task-{Guid.NewGuid():N}.xml");
        try
        {
            var escapedExe = EscapeXml(exePath);
            var xml = $"""
                <?xml version="1.0" encoding="UTF-16"?>
                <Task version="1.3" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
                  <RegistrationInfo>
                    <Description>ClashUI AutoStart</Description>
                  </RegistrationInfo>
                  <Principals>
                    <Principal id="Author">
                      <LogonType>InteractiveToken</LogonType>
                      <RunLevel>HighestAvailable</RunLevel>
                    </Principal>
                  </Principals>
                  <Settings>
                    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                    <Priority>7</Priority>
                  </Settings>
                  <Triggers>
                    <LogonTrigger>
                      <Enabled>true</Enabled>
                    </LogonTrigger>
                  </Triggers>
                  <Actions Context="Author">
                    <Exec>
                      <Command>{escapedExe}</Command>
                      <Arguments>--silent</Arguments>
                    </Exec>
                  </Actions>
                </Task>
                """;
            File.WriteAllText(tmpXml, xml, System.Text.Encoding.Unicode);
            return Run("schtasks", $"/Create /F /TN {TaskName} /XML \"{tmpXml}\"") == 0;
        }
        catch (Exception ex)
        {
            AppLog.Error("写入开机自启任务 XML 失败", ex);
            return false;
        }
        finally
        {
            try { if (File.Exists(tmpXml)) File.Delete(tmpXml); } catch { }
        }
    }

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;")
             .Replace("<", "&lt;")
             .Replace(">", "&gt;")
             .Replace("\"", "&quot;")
             .Replace("'", "&apos;");

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
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                WorkingDirectory = Environment.SystemDirectory,
            });
            if (proc is null) return -1;
            if (!proc.WaitForExit(15000))
            {
                try { proc.Kill(true); } catch { }
                return -1;
            }
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            AppLog.Error($"执行 {fileName} 失败", ex);
            return -1;
        }
    }
}
