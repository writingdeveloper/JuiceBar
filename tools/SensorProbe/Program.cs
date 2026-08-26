// SensorProbe — JuiceBar 1단계 검증 도구.
// 이 PC에서 LibreHardwareMonitorLib이 어떤 전력 센서를 실제로 읽어내는지 확인한다.
// PawnIO 미설치 상태에서 무엇이 null로 돌아오는지 파악하는 것이 주목적이다.

using System.Security.Principal;
using LibreHardwareMonitor.Hardware;

var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent())
    .IsInRole(WindowsBuiltInRole.Administrator);

Console.WriteLine($"관리자 권한 : {(elevated ? "예" : "아니오")}");
Console.WriteLine($"OS          : {Environment.OSVersion.VersionString} / .NET {Environment.Version}");
Console.WriteLine(new string('-', 72));

var computer = new Computer
{
    IsCpuEnabled = true,
    IsGpuEnabled = true,
    IsMemoryEnabled = true,
    IsMotherboardEnabled = true,
    IsStorageEnabled = true,
    IsBatteryEnabled = true,
    IsControllerEnabled = true,
};

computer.Open();

var visitor = new UpdateVisitor();
computer.Accept(visitor);

// 값이 안정될 시간을 준다. 첫 폴링은 0이나 null이 흔하다.
Thread.Sleep(1200);
computer.Accept(visitor);

int powerSensorCount = 0, nullPowerCount = 0;

foreach (var hardware in computer.Hardware)
{
    Console.WriteLine();
    Console.WriteLine($"[{hardware.HardwareType}] {hardware.Name}");

    DumpSensors(hardware, indent: "  ");

    foreach (var sub in hardware.SubHardware)
    {
        Console.WriteLine($"  └ [{sub.HardwareType}] {sub.Name}");
        DumpSensors(sub, indent: "      ");
    }
}

Console.WriteLine();
Console.WriteLine(new string('-', 72));
Console.WriteLine($"Power 센서 총 {powerSensorCount}개 중 값 없음(null) {nullPowerCount}개");

if (nullPowerCount > 0)
{
    Console.WriteLine();
    Console.WriteLine("→ null 이 있다면 PawnIO 드라이버 미설치 또는 권한 부족이 원인일 가능성이 큽니다.");
}

computer.Close();

void DumpSensors(IHardware hw, string indent)
{
    // 전력을 먼저 보여주고, 나머지는 참고용으로 뒤에 붙인다.
    var ordered = hw.Sensors
        .OrderBy(s => s.SensorType == SensorType.Power ? 0 : 1)
        .ThenBy(s => s.SensorType)
        .ThenBy(s => s.Name);

    foreach (var sensor in ordered)
    {
        bool isPower = sensor.SensorType == SensorType.Power;
        if (isPower)
        {
            powerSensorCount++;
            if (sensor.Value is null) nullPowerCount++;
        }

        // 온도/부하/전력/전압만 출력해서 노이즈를 줄인다.
        if (sensor.SensorType is not (SensorType.Power or SensorType.Temperature
            or SensorType.Load or SensorType.Voltage or SensorType.Energy))
            continue;

        string value = sensor.Value is float v ? v.ToString("F2") : "null";
        string unit = sensor.SensorType switch
        {
            SensorType.Power => "W",
            SensorType.Temperature => "°C",
            SensorType.Load => "%",
            SensorType.Voltage => "V",
            SensorType.Energy => "mWh",
            _ => "",
        };

        string mark = isPower ? "★ " : "  ";
        Console.WriteLine($"{indent}{mark}{sensor.SensorType,-12} {sensor.Name,-32} {value,10} {unit}");
    }
}

sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer) => computer.Traverse(this);

    public void VisitHardware(IHardware hardware)
    {
        hardware.Update();
        foreach (var sub in hardware.SubHardware) sub.Accept(this);
    }

    public void VisitSensor(ISensor sensor) { }
    public void VisitParameter(IParameter parameter) { }
}
