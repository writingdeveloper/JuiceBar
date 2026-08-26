using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace JuiceBar.Core.Power;

/// <summary>에너지 미터가 노출하는 채널 하나의 서술.</summary>
public sealed record EnergyMeterChannel(string DevicePath, int Index, string Name, string HardwareName)
{
    public string Id => $"emi:{DevicePath}#{Index}";
}

/// <summary>
/// Windows 에너지 미터(EMI, Energy Meter Interface)를 읽는다.
///
/// 이것이 JuiceBar 에서 가장 중요한 전력 소스다. 이유는 하나다 —
/// <b>드라이버 설치도 관리자 권한도 없이 CPU 패키지 전력을 읽을 수 있다.</b>
///
/// LibreHardwareMonitor 는 CPU 전력을 MSR 에서 직접 읽으므로 PawnIO 커널 드라이버와
/// 승격된 권한이 둘 다 있어야 한다. 둘 중 하나라도 없으면 CPU 가 0W 로 나오고,
/// 그러면 총합의 절반이 사용률 기반 추정으로 떨어진다.
///
/// 반면 Windows 는 전원 관리(PPM) 드라이버를 통해 같은 RAPL 카운터를 EMI 로 내보낸다.
/// 이건 문서화된 사용자 모드 API 라 평범한 권한으로 열린다.
/// 실제로 Ryzen 9 7950X 에서 승격 없이 확인한 값:
///
///   LibreHardwareMonitor "CPU Package"  →   0.00 W   (PawnIO 없음)
///   EMI "RAPL_Package0_PKG"             →  66.61 W
///
/// 에너지를 적분값(피코와트시)으로 주는 것도 이점이다. 순간 전력을 초당 한 번
/// 찍어 보는 것보다 정확하다 — 폴링 사이에 일어난 스파이크도 값에 들어 있다.
///
/// 하드웨어가 EMI 를 내보내지 않으면 채널이 하나도 없을 뿐, 아무것도 깨지지 않는다.
/// </summary>
public sealed class EnergyMeterReader : IDisposable
{
    // {45BD8344-7ED6-49CF-A440-C276C933B053} — emi.h 의 GUID_DEVICE_ENERGY_METER
    private static readonly Guid EnergyMeterInterface = new("45bd8344-7ed6-49cf-a440-c276c933b053");

    // CTL_CODE(FILE_DEVICE_UNKNOWN, n, METHOD_BUFFERED, FILE_READ_ACCESS)
    private const uint IoctlGetVersion = 0x224000;
    private const uint IoctlGetMetadataSize = 0x224004;
    private const uint IoctlGetMetadata = 0x224008;
    private const uint IoctlGetMeasurement = 0x22400C;

    /// <summary>EMI_NAME_MAX. OEM·모델 이름의 고정 길이(문자 수)다.</summary>
    private const int NameMax = 16;

    private readonly List<Device> _devices = [];
    private bool _disposed;

    /// <summary>이 PC 에서 에너지 미터를 하나라도 읽을 수 있는지.</summary>
    public bool IsAvailable => _devices.Count > 0;

    public EnergyMeterReader()
    {
        try
        {
            Open();
        }
        catch (Exception)
        {
            // 에너지 미터는 있으면 좋은 것이지 필수가 아니다.
            // 무슨 일이 있어도 앱 시작을 막지 않는다.
            Close();
        }

        // 전력은 두 시점의 에너지 차이로만 구할 수 있다. 지금 한 번 읽어 기준을 잡아 두면
        // 첫 폴링부터 값이 나온다. 그러지 않으면 시작 직후 1초 동안 CPU 가 0W 로 보인다.
        Read();
    }

    private void Open()
    {
        foreach (string path in EnumerateInterfacePaths())
        {
            var handle = NativeMethods.CreateFile(
                path,
                NativeMethods.GenericRead,
                NativeMethods.FileShareReadWrite,
                IntPtr.Zero,
                NativeMethods.OpenExisting,
                NativeMethods.FileAttributeNormal,
                IntPtr.Zero);

            if (handle.IsInvalid)
            {
                handle.Dispose();
                continue;
            }

            var device = Describe(handle, path);

            if (device is null)
            {
                handle.Dispose();
                continue;
            }

            _devices.Add(device);
        }
    }

    /// <summary>장치를 열어 채널 구성을 읽는다. 쓸 채널이 하나도 없으면 null 을 준다.</summary>
    private static Device? Describe(SafeFileHandle handle, string path)
    {
        var versionBuffer = new byte[2];
        if (!Control(handle, IoctlGetVersion, versionBuffer)) return null;

        ushort version = BitConverter.ToUInt16(versionBuffer);

        var sizeBuffer = new byte[4];
        if (!Control(handle, IoctlGetMetadataSize, sizeBuffer)) return null;

        int metadataSize = BitConverter.ToInt32(sizeBuffer);
        if (metadataSize is <= 0 or > 64 * 1024) return null;

        var metadata = new byte[metadataSize];
        if (!Control(handle, IoctlGetMetadata, metadata)) return null;

        var described = ParseMetadata(metadata, version);
        if (described is null || described.ChannelNames.Count == 0) return null;

        // 합산에 쓸 수 있는 채널만 남긴다. 코어별 값은 패키지 안에 이미 들어 있어
        // 그대로 두면 목록만 어지럽고 잘못 더할 위험이 생긴다.
        var usable = new List<EnergyMeterChannel>();
        for (int i = 0; i < described.ChannelNames.Count; i++)
        {
            string name = described.ChannelNames[i];
            if (IsSubsumedByPackage(name)) continue;

            usable.Add(new EnergyMeterChannel(path, i, name, described.HardwareName));
        }

        if (usable.Count == 0) return null;

        return new Device(handle, described.ChannelNames.Count, usable);
    }

    /// <summary>
    /// 지금 각 채널의 전력을 계산해 돌려준다.
    ///
    /// 처음 부르면 기준점만 잡고 0W 를 준다. 두 번째부터 실제 값이 나온다.
    /// </summary>
    public IReadOnlyList<PowerChannel> Read()
    {
        if (_disposed || _devices.Count == 0) return [];

        var result = new List<PowerChannel>();

        foreach (var device in _devices)
        {
            var buffer = new byte[MeasurementSize * device.TotalChannelCount];
            if (!Control(device.Handle, IoctlGetMeasurement, buffer)) continue;

            foreach (var channel in device.Channels)
            {
                int offset = channel.Index * MeasurementSize;
                ulong energy = BitConverter.ToUInt64(buffer, offset);
                ulong time = BitConverter.ToUInt64(buffer, offset + 8);

                double watts = 0;

                if (device.Previous.TryGetValue(channel.Index, out var previous))
                    watts = ComputeWatts(previous.Energy, previous.Time, energy, time) ?? 0;

                device.Previous[channel.Index] = (energy, time);

                result.Add(new PowerChannel(
                    Id: channel.Id,
                    Label: FriendlyChannelName(channel.Name),
                    HardwareName: channel.HardwareName,
                    Kind: Classify(channel.Name),
                    Watts: watts));
            }
        }

        return result;
    }

    // ─────────────── 순수 계산 (하드웨어 없이 검증 가능) ───────────────

    /// <summary>EMI_CHANNEL_MEASUREMENT_DATA — ULONGLONG 두 개.</summary>
    internal const int MeasurementSize = 16;

    /// <summary>
    /// 두 측정 사이의 평균 전력(W).
    ///
    /// 에너지는 피코와트시, 시간은 100ns 단위다.
    ///
    ///   W = (ΔE × 1e-12 [Wh] × 3600 [J/Wh]) / (ΔT × 1e-7 [s])
    ///     = ΔE × 0.036 / ΔT
    ///
    /// 검산: 1 Wh(=1e12 pWh)를 한 시간(=3.6e10 × 100ns) 동안 쓰면 1e12 × 0.036 / 3.6e10 = 1 W.
    ///
    /// 값을 믿을 수 없으면 null 을 준다 — 시간이 흐르지 않았거나(같은 시점을 두 번 읽음),
    /// 에너지가 거꾸로 갔거나(장치 재초기화), 물리적으로 말이 안 되는 크기일 때다.
    /// </summary>
    internal static double? ComputeWatts(ulong previousEnergy, ulong previousTime, ulong energy, ulong time)
    {
        if (time <= previousTime) return null;
        if (energy < previousEnergy) return null;

        double watts = (energy - previousEnergy) * 0.036 / (time - previousTime);

        // 데스크톱 CPU 패키지가 2kW 를 쓸 일은 없다. 이 정도면 해석이 틀린 것이다.
        if (double.IsNaN(watts) || watts < 0 || watts > 2000) return null;

        return watts;
    }

    /// <summary>메타데이터에서 읽어 낸 장치 서술.</summary>
    internal sealed record MeterMetadata(string HardwareName, IReadOnlyList<string> ChannelNames);

    /// <summary>
    /// EMI_METADATA_V1 / V2 를 해석한다. emi.h 의 배치를 그대로 따른다.
    ///
    ///   V1: MeasurementUnit(4) OEM(32) Model(32) Revision(2) NameSize(2) Name(가변)
    ///   V2: OEM(32) Model(32) Revision(2) ChannelCount(2) 그리고 채널 배열
    ///       채널 하나: MeasurementUnit(4) NameSize(2) Name(가변)
    ///
    /// 채널은 4바이트 경계로 맞추지 않는다 — emi.h 의 EMI_CHANNEL_V2_NEXT_CHANNEL 매크로가
    /// 길이를 그대로 더하기 때문이다. 그래서 여기서도 올림 없이 그대로 넘어간다.
    /// </summary>
    internal static MeterMetadata? ParseMetadata(byte[] buffer, ushort version)
    {
        try
        {
            if (version == 1)
            {
                if (buffer.Length < 72) return null;

                string oem = ReadFixedString(buffer, 4, NameMax);
                string model = ReadFixedString(buffer, 4 + (NameMax * 2), NameMax);
                int nameSize = BitConverter.ToUInt16(buffer, 70);

                if (nameSize < 0 || 72 + nameSize > buffer.Length) return null;

                return new MeterMetadata(
                    Describe(oem, model),
                    [ReadString(buffer, 72, nameSize)]);
            }

            if (buffer.Length < 68) return null;

            string oem2 = ReadFixedString(buffer, 0, NameMax);
            string model2 = ReadFixedString(buffer, NameMax * 2, NameMax);
            int count = BitConverter.ToUInt16(buffer, 66);

            // 채널 수가 터무니없으면 해석이 어긋난 것이다. 통째로 버린다.
            if (count is <= 0 or > 256) return null;

            var names = new List<string>(count);
            int offset = 68;

            for (int i = 0; i < count; i++)
            {
                if (offset + 6 > buffer.Length) return null;

                int nameSize = BitConverter.ToUInt16(buffer, offset + 4);
                if (nameSize < 0 || offset + 6 + nameSize > buffer.Length) return null;

                names.Add(ReadString(buffer, offset + 6, nameSize));
                offset += 6 + nameSize;
            }

            return new MeterMetadata(Describe(oem2, model2), names);
        }
        catch (Exception)
        {
            return null;
        }

        static string Describe(string oem, string model)
        {
            string joined = string.Join(' ', new[] { oem, model }.Where(s => s.Length > 0));
            return joined.Length > 0 ? $"Energy Meter ({joined})" : "Energy Meter";
        }
    }

    /// <summary>
    /// 패키지 값 안에 이미 포함된 채널인지.
    ///
    /// RAPL 도메인은 겹친다. PKG 가 코어(PP0/CORE)와 내장 GPU(PP1)를 품고 있어서
    /// 함께 더하면 같은 전력을 두 번 세게 된다. DRAM 은 PKG 밖이라 예외다.
    /// </summary>
    internal static bool IsSubsumedByPackage(string channelName)
    {
        if (channelName.EndsWith("_CORE", StringComparison.OrdinalIgnoreCase)) return true;
        if (channelName.EndsWith("_PP0", StringComparison.OrdinalIgnoreCase)) return true;
        if (channelName.EndsWith("_PP1", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>CPU 패키지 전체를 재는 채널인지. 이게 있으면 CPU 를 실측한 것이다.</summary>
    internal static bool IsPackageChannel(string channelName)
        => channelName.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase);

    internal static ChannelKind Classify(string channelName)
    {
        if (IsPackageChannel(channelName)) return ChannelKind.Cpu;

        // RAPL 이 아닌 이름은 OEM 이 붙인 레일이다(노트북·Surface 계열).
        // 무엇을 재는지 알 수 없으므로 CPU 나 GPU 로 단정하지 않는다.
        return ChannelKind.Other;
    }

    /// <summary>설정 화면에 그대로 내보내기엔 투박한 이름을 다듬는다.</summary>
    internal static string FriendlyChannelName(string channelName)
    {
        if (IsPackageChannel(channelName)) return "CPU Package";
        if (channelName.EndsWith("_DRAM", StringComparison.OrdinalIgnoreCase)) return "DRAM";

        return channelName;
    }

    // ─────────────── Win32 ───────────────

    private sealed class Device(
        SafeFileHandle handle,
        int totalChannelCount,
        IReadOnlyList<EnergyMeterChannel> channels)
    {
        public SafeFileHandle Handle { get; } = handle;

        /// <summary>측정 버퍼 크기를 정하려면 걸러 내기 전의 전체 채널 수가 필요하다.</summary>
        public int TotalChannelCount { get; } = totalChannelCount;

        public IReadOnlyList<EnergyMeterChannel> Channels { get; } = channels;

        public Dictionary<int, (ulong Energy, ulong Time)> Previous { get; } = [];
    }

    private static unsafe bool Control(SafeFileHandle handle, uint code, byte[] output)
    {
        fixed (byte* pointer = output)
        {
            return NativeMethods.DeviceIoControl(
                handle, code, IntPtr.Zero, 0, (IntPtr)pointer, output.Length, out _, IntPtr.Zero);
        }
    }

    private static List<string> EnumerateInterfacePaths()
    {
        var paths = new List<string>();
        var guid = EnergyMeterInterface;

        IntPtr set = NativeMethods.SetupDiGetClassDevs(
            ref guid, IntPtr.Zero, IntPtr.Zero, NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);

        if (set == NativeMethods.InvalidHandle) return paths;

        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new NativeMethods.DeviceInterfaceData
                {
                    Size = Marshal.SizeOf<NativeMethods.DeviceInterfaceData>(),
                };

                if (!NativeMethods.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data))
                    break;

                NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out int needed, IntPtr.Zero);
                if (needed <= 0) continue;

                IntPtr detail = Marshal.AllocHGlobal(needed);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA_W 의 cbSize 는 구조체 전체가 아니라
                    // 헤더 크기다. 64비트에서는 8, 32비트에서는 6이다.
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);

                    if (NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, needed, out _, IntPtr.Zero))
                    {
                        string? path = Marshal.PtrToStringUni(detail + 4);
                        if (!string.IsNullOrEmpty(path)) paths.Add(path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }

        return paths;
    }

    private static string ReadFixedString(byte[] buffer, int offset, int charCount)
        => ReadString(buffer, offset, charCount * 2);

    private static string ReadString(byte[] buffer, int offset, int byteCount)
    {
        if (byteCount <= 0 || offset + byteCount > buffer.Length) return string.Empty;

        string text = System.Text.Encoding.Unicode.GetString(buffer, offset, byteCount);
        int terminator = text.IndexOf('\0');

        return (terminator >= 0 ? text[..terminator] : text).Trim();
    }

    private void Close()
    {
        foreach (var device in _devices) device.Handle.Dispose();
        _devices.Clear();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
    }

    private static class NativeMethods
    {
        public const uint GenericRead = 0x80000000;
        public const uint FileShareReadWrite = 3;
        public const uint OpenExisting = 3;
        public const uint FileAttributeNormal = 0x80;

        public const uint DigcfPresent = 0x02;
        public const uint DigcfDeviceInterface = 0x10;

        public static readonly IntPtr InvalidHandle = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct DeviceInterfaceData
        {
            public int Size;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SetupDiGetClassDevs(
            ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiEnumDeviceInterfaces(
            IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
            uint memberIndex, ref DeviceInterfaceData data);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiGetDeviceInterfaceDetail(
            IntPtr deviceInfoSet, ref DeviceInterfaceData data, IntPtr detail,
            int detailSize, out int required, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFile(
            string fileName, uint access, uint share, IntPtr security,
            uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeviceIoControl(
            SafeFileHandle handle, uint code, IntPtr input, int inputSize,
            IntPtr output, int outputSize, out int returned, IntPtr overlapped);
    }
}
