using System.Diagnostics;
using System.IO;
using System.Text;

namespace JuiceBar.Services;

/// <summary>
/// 로그온 시 자동 시작.
///
/// 흔히 쓰는 레지스트리 Run 키로는 안 된다 — 거기서 실행된 프로세스는 승격되지 않아
/// 매번 UAC 창이 뜨거나 그냥 실패한다. 작업 스케줄러에 "가장 높은 권한으로 실행"
/// 작업을 등록해야 조용히 승격된 채로 뜬다.
/// </summary>
public static class AutoStart
{
    private const string TaskName = "JuiceBar";

    public static bool IsEnabled()
        => RunSchTasks($"/query /tn \"{TaskName}\"").ExitCode == 0;

    public static bool Enable(string executablePath)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"juicebar-task-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(executablePath), new UnicodeEncoding(false, true));

            var result = RunSchTasks($"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f");
            return result.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch (Exception) { /* 임시 파일이라 실패해도 무해하다 */ }
        }
    }

    public static bool Disable()
        => RunSchTasks($"/delete /tn \"{TaskName}\" /f").ExitCode == 0;

    private static string BuildTaskXml(string executablePath) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>JuiceBar - 실시간 전력 및 전기요금 표시</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Environment.UserDomainName}\{Environment.UserName}</UserId>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Environment.UserDomainName}\{Environment.UserName}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
            <WakeToRun>false</WakeToRun>
            <!-- 상주 앱이므로 실행 시간 제한을 두지 않는다. -->
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{System.Security.SecurityElement.Escape(executablePath)}</Command>
            </Exec>
          </Actions>
        </Task>
        """;

    private static (int ExitCode, string Output) RunSchTasks(string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null) return (-1, string.Empty);

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            return (process.ExitCode, output);
        }
        catch (Exception)
        {
            return (-1, string.Empty);
        }
    }
}
