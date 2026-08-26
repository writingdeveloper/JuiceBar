using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace JuiceBar.Core.Power;

/// <summary>배터리 상태. 노트북 자동 캘리브레이션의 입력이다.</summary>
public sealed record BatteryState(
    bool Present,
    bool OnBattery,
    double DischargeWatts,
    double ChargeWatts);

/// <summary>한 번 폴링해서 얻은 원시 센서 값.</summary>
public sealed record SensorSnapshot(
    IReadOnlyList<PowerChannel> Channels,
    BatteryState Battery,
    double CpuLoadPercent);

/// <summary>
/// LibreHardwareMonitorLib 위에 얹은 얇은 래퍼.
///
/// 하드웨어마다 센서 이름과 의미가 제각각이라 여기서는 해석하지 않고 채널을 그대로 열거만 한다.
/// 무엇을 합산할지는 장비 프로필이 정한다.
/// </summary>
public sealed class SensorReader : IDisposable
{
    private readonly Computer _computer;
    private readonly UpdateVisitor _visitor = new();
    private readonly EnergyMeterReader _energyMeter = new();
    private bool _disposed;

    /// <summary>
    /// Windows 에너지 미터로 CPU 전력을 읽을 수 있는지.
    /// 이게 참이면 PawnIO 없이도 정밀 측정이 된다.
    /// </summary>
    public bool HasEnergyMeter => _energyMeter.IsAvailable;

    public SensorReader()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsBatteryEnabled = true,

            // 전력과 무관한 하위 시스템은 켜지 않는다. 폴링 비용과 드라이버 접촉면을 줄인다.
            IsMemoryEnabled = false,
            IsMotherboardEnabled = false,
            IsStorageEnabled = false,
            IsControllerEnabled = false,
            IsNetworkEnabled = false,
            IsPsuEnabled = false,
        };

        _computer.Open();
        DisableSensorHistory();
    }

    /// <summary>
    /// LibreHardwareMonitor 는 센서마다 지난 값을 메모리에 쌓아 둔다.
    ///
    /// 하루 종일 떠 있는 트레이 앱에서는 이게 계속 불어난다 — 센서 100개를 1초마다
    /// 읽으면 하루에 수백만 개다. JuiceBar 는 필요한 이력을 SQLite 에 따로 기록하므로
    /// 이 안쪽 버퍼는 쓸 데가 없다. 창을 0으로 두어 아예 모으지 않게 한다.
    ///
    /// 하드웨어가 나중에 나타나는 경우(외장 GPU 연결 등)도 있어 폴링할 때마다 다시 적용한다.
    /// </summary>
    private void DisableSensorHistory()
    {
        foreach (var hardware in _computer.Hardware)
        {
            Apply(hardware);
            foreach (var sub in hardware.SubHardware) Apply(sub);
        }

        static void Apply(IHardware hw)
        {
            foreach (var sensor in hw.Sensors)
                sensor.ValuesTimeWindow = TimeSpan.Zero;
        }
    }

    public SensorSnapshot Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _computer.Accept(_visitor);
        DisableSensorHistory();

        var channels = new List<PowerChannel>();
        bool batteryPresent = false;
        double dischargeWatts = 0, chargeWatts = 0;
        double cpuLoad = 0;

        foreach (var hardware in _computer.Hardware)
        {
            if (hardware.HardwareType == HardwareType.Battery)
                batteryPresent = true;

            CollectFrom(hardware);

            foreach (var sub in hardware.SubHardware)
                CollectFrom(sub);
        }

        // 에너지 미터 채널을 같은 목록에 얹는다. 무엇을 합산할지는 프로필이 정하므로
        // 여기서는 출처를 가리지 않고 나란히 내놓기만 한다.
        channels.AddRange(_energyMeter.Read());

        return new SensorSnapshot(
            channels,
            new BatteryState(batteryPresent, IsRunningOnBattery(), dischargeWatts, chargeWatts),
            cpuLoad);

        void CollectFrom(IHardware hw)
        {
            foreach (var sensor in hw.Sensors)
            {
                if (sensor.SensorType == SensorType.Load
                    && hw.HardwareType == HardwareType.Cpu
                    && sensor.Name == "CPU Total")
                {
                    cpuLoad = sensor.Value ?? 0;
                    continue;
                }

                if (sensor.SensorType != SensorType.Power) continue;

                double watts = sensor.Value ?? 0;
                var kind = ClassifyKind(hw.HardwareType);

                if (kind == ChannelKind.Battery)
                {
                    // LHM은 방전과 충전을 각각 다른 센서로 낸다.
                    if (sensor.Name.Contains("Discharge", StringComparison.OrdinalIgnoreCase))
                        dischargeWatts = watts;
                    else if (sensor.Name.Contains("Charge", StringComparison.OrdinalIgnoreCase))
                        chargeWatts = watts;
                }

                channels.Add(new PowerChannel(
                    Id: sensor.Identifier.ToString(),
                    Label: sensor.Name,
                    HardwareName: hw.Name,
                    Kind: kind,
                    Watts: watts));
            }
        }
    }

    private static ChannelKind ClassifyKind(HardwareType type) => type switch
    {
        HardwareType.Cpu => ChannelKind.Cpu,
        HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel => ChannelKind.Gpu,
        HardwareType.Battery => ChannelKind.Battery,
        _ => ChannelKind.Other,
    };

    /// <summary>
    /// 채널 목록에서 기본 합산 대상을 고른다.
    ///
    /// 이중 합산을 피하는 것이 전부다. 같은 전력이 여러 센서에 나타나기 때문이다.
    ///
    ///   · 코어별 센서는 패키지 값 안에 들어 있다.
    ///   · 내장 GPU는 CPU 패키지 안에 있다. Ryzen 7950X 에서 실측해 보면
    ///     CPU Package 62.7W 에 iGPU 가 "GPU Core 35W + GPU SoC 11W" 로 또 잡힌다.
    ///     이걸 함께 더하면 실제보다 46W 가 부풀려진다.
    ///
    /// 하드웨어마다 센서 구성이 달라 이 판단이 틀릴 수 있으므로,
    /// 설정 화면에서 채널을 직접 켜고 끌 수 있게 열어 두었다.
    /// </summary>
    public static HashSet<string> DefaultSelection(IReadOnlyList<PowerChannel> channels)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);

        var meterChannels = channels.Where(c => IsEnergyMeterChannel(c.Id)).ToList();
        var sensorChannels = channels.Where(c => !IsEnergyMeterChannel(c.Id)).ToList();

        // 에너지 미터가 CPU 패키지를 실제로 재고 있는지. 채널이 있어도 0W 만 낸다면
        // 그건 재는 게 아니다 — 그럴 때 LibreHardwareMonitor 값을 밀어내면 손해다.
        bool meterMeasuresCpu = meterChannels.Any(c => c.Kind == ChannelKind.Cpu && c.Watts > 0);

        // 에너지 미터 채널은 이미 겹치지 않는 것만 남아 있다(코어별·PP0·PP1 은 걸러 냈다).
        foreach (var channel in meterChannels)
        {
            if (channel.Kind == ChannelKind.Cpu && !meterMeasuresCpu) continue;
            selected.Add(channel.Id);
        }

        // CPU 패키지 센서가 살아 있으면 내장 GPU 몫은 이미 그 안에 포함돼 있다.
        bool cpuPackageAvailable = meterMeasuresCpu || sensorChannels.Any(c =>
            c.Kind == ChannelKind.Cpu && IsPackageSensor(c.Label) && c.Watts > 0);

        foreach (var group in sensorChannels.GroupBy(c => c.HardwareName))
        {
            var items = group.ToList();
            var kind = items[0].Kind;

            if (kind == ChannelKind.Battery) continue;

            // 에너지 미터와 LibreHardwareMonitor 는 같은 RAPL 카운터를 읽는다.
            // 둘 다 더하면 CPU 전력을 정확히 두 번 세게 된다.
            if (kind == ChannelKind.Cpu && meterMeasuresCpu) continue;

            if (kind == ChannelKind.Gpu
                && cpuPackageAvailable
                && IsLikelyIntegratedGpu(group.Key))
                continue;

            // 패키지 단위 센서가 있으면 그것만 쓴다. 나머지는 그 안에 포함된 값이다.
            var package = items.FirstOrDefault(c => IsPackageSensor(c.Label));
            if (package is not null)
            {
                selected.Add(package.Id);
                continue;
            }

            // 패키지 센서가 없는 경우(일부 AMD)는 코어별이 아닌 레일 센서들을 합친다.
            foreach (var item in items)
                if (!IsPerCoreSensor(item.Label))
                    selected.Add(item.Id);
        }

        return selected;
    }

    /// <summary>채널이 Windows 에너지 미터에서 온 것인지.</summary>
    internal static bool IsEnergyMeterChannel(string channelId)
        => channelId.StartsWith("emi:", StringComparison.Ordinal);

    /// <summary>
    /// 이름으로 내장 그래픽을 가려낸다.
    ///
    /// LibreHardwareMonitor 는 내장인지 외장인지를 알려주지 않아 이름에 기댈 수밖에 없다.
    /// 내장 그래픽은 모델 번호 없이 "Radeon(TM) Graphics", "UHD Graphics" 처럼 불리고,
    /// 외장 카드는 "RTX 4080 SUPER", "RX 7900 XTX" 처럼 번호가 붙는다.
    /// </summary>
    internal static bool IsLikelyIntegratedGpu(string hardwareName)
    {
        string[] markers =
        [
            "(tm) graphics",     // AMD Ryzen 내장 (예: AMD Radeon(TM) Graphics)
            "uhd graphics",      // Intel
            "hd graphics",       // Intel 구형
            "iris",              // Intel Iris / Iris Xe
            "integrated",
            "radeon graphics",   // 일부 AMD APU 표기
        ];

        foreach (string marker in markers)
            if (hardwareName.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static bool IsPackageSensor(string label)
        => label.Equals("Package", StringComparison.OrdinalIgnoreCase)
        || label.Equals("GPU Package", StringComparison.OrdinalIgnoreCase)
        || label.Equals("CPU Package", StringComparison.OrdinalIgnoreCase);

    private static bool IsPerCoreSensor(string label)
        => label.Contains("Core #", StringComparison.OrdinalIgnoreCase);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>배터리로 구동 중인지. AC 상태 0이 배터리 구동을 뜻한다.</summary>
    public static bool IsRunningOnBattery()
        => GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _energyMeter.Dispose();
        _computer.Close();
    }

    private sealed class UpdateVisitor : IVisitor
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
}
