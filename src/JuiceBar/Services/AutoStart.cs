using System.Diagnostics;
using System.IO;
using System.Text;
using JuiceBar.Core.Platform;
using Microsoft.Win32;

namespace JuiceBar.Services;

/// <summary>자동 시작을 어디에 등록할지.</summary>
public enum AutoStartMechanism
{
    /// <summary>HKCU 의 Run 키. 권한이 필요 없지만 승격된 채로 띄우지는 못한다.</summary>
    RegistryRunKey,

    /// <summary>작업 스케줄러. 승격된 채로 조용히 띄울 수 있지만 등록에 관리자 권한이 필요하다.</summary>
    ScheduledTask,
}

/// <summary>
/// 로그온 시 자동 시작.
///
/// 방법이 둘인 이유는 하나다 — <b>어느 쪽도 혼자서는 두 경우를 다 못 덮는다.</b>
///
///   · 레지스트리 Run 키는 누구나 쓸 수 있지만 승격된 프로세스를 띄우지 못한다.
///   · 작업 스케줄러는 승격해서 띄울 수 있지만, <b>등록 자체에 관리자 권한이 필요하다</b>
///     (비승격 사용자가 만들려 하면 액세스 거부가 난다. 실제로 확인했다).
///
/// JuiceBar 는 대개 승격 없이 돈다 — Windows 에너지 미터 덕분에 그럴 필요가 없어졌다.
/// 그럴 때는 Run 키가 맞다. 미터가 없어 드라이버로 CPU 를 읽어야 하는 기기에서만
/// 승격된 채로 돌고, 그때는 작업 스케줄러여야 로그온 때마다 UAC 창이 뜨지 않는다.
///
/// 그래서 "지금 내가 승격되어 있는가"로 방법을 고른다. 사용자가 앱을 쓰는 방식과
/// 자동 시작이 앱을 띄우는 방식이 어긋나지 않게 하려는 것이다.
/// </summary>
public static class AutoStart
{
    private const string TaskName = "JuiceBar";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "JuiceBar";

    /// <summary>지금 상태에서 쓸 수 있는 방법.</summary>
    public static AutoStartMechanism Preferred(bool isElevated)
        => isElevated ? AutoStartMechanism.ScheduledTask : AutoStartMechanism.RegistryRunKey;

    public static bool IsEnabled() => HasRunKey() || HasTask();

    /// <summary>
    /// 자동 시작을 켠다.
    ///
    /// 예전 버전은 늘 작업 스케줄러를 썼다. 승격 없이 도는 지금은 그 작업을 지울 수
    /// 없을 수도 있는데, 남아 있어도 해롭지는 않다 — 앱이 승격된 채로 뜰 뿐이다.
    /// 지우지 못했다고 해서 실패로 보고하지는 않는다.
    /// </summary>
    public static bool Enable(string executablePath)
    {
        if (Preferred(Elevation.IsElevated) == AutoStartMechanism.ScheduledTask)
        {
            if (!EnableTask(executablePath)) return false;

            RemoveRunKey();
            return true;
        }

        if (!EnableRunKey(executablePath)) return false;

        RemoveTask();
        return true;
    }

    /// <summary>
    /// 자동 시작을 끈다.
    ///
    /// 승격 없이 돌면서 예전에 만들어 둔 작업을 지워야 하는 경우가 있다. 그건 권한이
    /// 없으면 실패하므로, 그때는 실패로 알려서 사용자가 이유를 알 수 있게 한다.
    /// 조용히 성공했다고 하면 껐다고 믿은 채로 계속 자동 시작된다.
    /// </summary>
    public static bool Disable()
    {
        RemoveRunKey();
        RemoveTask();

        return !IsEnabled();
    }

    // ─────────────── 레지스트리 Run 키 ───────────────

    private static bool HasRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool EnableRunKey(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            // 경로에 공백이 있으면 따옴표가 없을 때 잘린 경로로 실행된다.
            key.SetValue(RunValueName, $"\"{executablePath}\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            key?.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception)
        {
            // 없거나 못 지웠다. IsEnabled 가 진실을 말해 준다.
        }
    }

    // ─────────────── 작업 스케줄러 ───────────────

    private static bool HasTask()
        => RunSchTasks($"/query /tn \"{TaskName}\"").ExitCode == 0;

    private static bool EnableTask(string executablePath)
    {
        string xmlPath = Path.Combine(Path.GetTempPath(), $"juicebar-task-{Guid.NewGuid():N}.xml");

        try
        {
            File.WriteAllText(xmlPath, BuildTaskXml(executablePath), new UnicodeEncoding(false, true));

            return RunSchTasks($"/create /tn \"{TaskName}\" /xml \"{xmlPath}\" /f").ExitCode == 0;
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

    private static void RemoveTask()
    {
        if (!HasTask()) return;

        RunSchTasks($"/delete /tn \"{TaskName}\" /f");
    }

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
