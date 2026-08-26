using JuiceBar.Core.Power;

namespace JuiceBar.Tests;

/// <summary>
/// Windows 에너지 미터에서 온 바이트를 전력으로 바꾸는 부분.
///
/// 여기가 틀리면 값이 조용히 어긋난다 — 앱은 멀쩡히 돌면서 요금만 몇 배로 나온다.
/// 그래서 하드웨어 없이 확인할 수 있는 형태로 떼어 두고, 물리적으로 자명한 값으로 검산한다.
/// </summary>
public class EnergyMeterReaderTests
{
    // ─────────────── 전력 환산 ───────────────

    /// <summary>1시간을 100ns 단위로.</summary>
    private const ulong OneHour = 36_000_000_000;

    /// <summary>1초를 100ns 단위로.</summary>
    private const ulong OneSecond = 10_000_000;

    /// <summary>1와트시를 피코와트시로.</summary>
    private const ulong OneWattHour = 1_000_000_000_000;

    [Fact]
    public void One_watt_hour_over_one_hour_is_one_watt()
    {
        // 단위 환산이 맞는지 보는 가장 단순한 값이다.
        // 상수 하나만 어긋나도 여기서 1000배씩 빗나간다.
        var watts = EnergyMeterReader.ComputeWatts(0, 0, OneWattHour, OneHour);

        Assert.Equal(1.0, watts!.Value, precision: 9);
    }

    [Fact]
    public void Fifty_watt_hours_over_half_an_hour_is_a_hundred_watts()
    {
        var watts = EnergyMeterReader.ComputeWatts(
            previousEnergy: 0,
            previousTime: 0,
            energy: 50 * OneWattHour,
            time: OneHour / 2);

        Assert.Equal(100.0, watts!.Value, precision: 9);
    }

    [Fact]
    public void The_reading_is_the_difference_not_the_absolute_value()
    {
        // 미터는 부팅 이후 누적값을 준다. 이미 한참 쌓인 상태에서 시작해도
        // 같은 증가분이면 같은 전력이 나와야 한다.
        var fromZero = EnergyMeterReader.ComputeWatts(0, 0, OneWattHour, OneHour);

        var fromLater = EnergyMeterReader.ComputeWatts(
            previousEnergy: 900 * OneWattHour,
            previousTime: 500 * OneHour,
            energy: 901 * OneWattHour,
            time: 501 * OneHour);

        Assert.Equal(fromZero!.Value, fromLater!.Value, precision: 9);
    }

    [Fact]
    public void A_realistic_cpu_package_reading_lands_where_it_should()
    {
        // 이 PC(Ryzen 9 7950X)에서 승격 없이 실제로 읽은 값에 해당하는 크기다.
        // 66W 를 1초 동안 쓰면 에너지가 얼마나 늘어야 하는지 거꾸로 계산해 넣는다.
        ulong energy = (ulong)(66.0 * OneSecond / 0.036);

        var watts = EnergyMeterReader.ComputeWatts(0, 0, energy, OneSecond);

        Assert.Equal(66.0, watts!.Value, precision: 3);
    }

    [Fact]
    public void Without_time_passing_there_is_no_power_to_report()
    {
        // 같은 시점을 두 번 읽으면 0으로 나누게 된다.
        Assert.Null(EnergyMeterReader.ComputeWatts(0, OneHour, OneWattHour, OneHour));
    }

    [Fact]
    public void Time_going_backwards_is_rejected()
    {
        Assert.Null(EnergyMeterReader.ComputeWatts(0, OneHour, OneWattHour, OneHour - 1));
    }

    [Fact]
    public void Energy_going_backwards_is_rejected()
    {
        // 장치가 다시 초기화되면 누적값이 0부터 시작한다.
        // 그 구간을 빼면 부호가 뒤집혀 엉뚱한 값이 된다.
        Assert.Null(EnergyMeterReader.ComputeWatts(
            previousEnergy: 100 * OneWattHour,
            previousTime: 0,
            energy: 1,
            time: OneHour));
    }

    [Fact]
    public void An_impossible_wattage_is_rejected_rather_than_shown()
    {
        // 5kW 는 가정용 PC 한 대의 값일 수 없다. 해석이 어긋났다는 뜻이므로
        // 그대로 내보내면 그 순간의 요금이 통째로 망가진다.
        var watts = EnergyMeterReader.ComputeWatts(
            previousEnergy: 0,
            previousTime: 0,
            energy: 5000 * OneWattHour,
            time: OneHour);

        Assert.Null(watts);
    }

    // ─────────────── 채널 분류 ───────────────

    [Theory]
    // RAPL 도메인은 겹친다. 코어와 내장 GPU 는 패키지 값 안에 이미 들어 있다.
    [InlineData("RAPL_Package0_Core0_CORE")]
    [InlineData("RAPL_Package0_Core15_CORE")]
    [InlineData("RAPL_Package0_PP0")]
    [InlineData("RAPL_Package0_PP1")]
    public void Channels_already_inside_the_package_are_not_summed(string name)
    {
        Assert.True(EnergyMeterReader.IsSubsumedByPackage(name));
    }

    [Theory]
    [InlineData("RAPL_Package0_PKG")]
    // DRAM 은 패키지 밖이라 따로 더해야 한다.
    [InlineData("RAPL_Package0_DRAM")]
    // OEM 이 붙인 레일은 무엇을 재는지 알 수 없지만, 겹친다고 단정할 근거도 없다.
    [InlineData("SYSTEM")]
    [InlineData("Battery Rail")]
    public void Channels_outside_the_package_are_kept(string name)
    {
        Assert.False(EnergyMeterReader.IsSubsumedByPackage(name));
    }

    [Fact]
    public void A_package_channel_counts_as_cpu_power()
    {
        Assert.True(EnergyMeterReader.IsPackageChannel("RAPL_Package0_PKG"));
        Assert.Equal(ChannelKind.Cpu, EnergyMeterReader.Classify("RAPL_Package0_PKG"));
    }

    [Fact]
    public void An_unknown_oem_rail_is_not_claimed_to_be_cpu_or_gpu()
    {
        // 이름만 보고 CPU 라고 우기면 "CPU 몫"이 통째로 틀어진다. 모르면 기타로 둔다.
        Assert.Equal(ChannelKind.Other, EnergyMeterReader.Classify("SYSTEM"));
        Assert.Equal(ChannelKind.Other, EnergyMeterReader.Classify("RAPL_Package0_DRAM"));
    }

    [Theory]
    [InlineData("RAPL_Package0_PKG", "CPU Package")]
    [InlineData("RAPL_Package0_DRAM", "DRAM")]
    // 알아볼 수 없는 이름은 지어내지 말고 그대로 보여 준다.
    [InlineData("SYSTEM", "SYSTEM")]
    public void Channel_names_are_tidied_for_the_settings_list(string raw, string shown)
    {
        Assert.Equal(shown, EnergyMeterReader.FriendlyChannelName(raw));
    }

    // ─────────────── 메타데이터 해석 ───────────────
    //
    // emi.h 의 구조체 배치를 그대로 흉내 낸 바이트를 만들어 넣는다.
    // 실제 장치에서 온 바이트와 같은 모양이므로, 여기가 통과하면 해석이 맞는 것이다.

    private static byte[] BuildV2(string oem, string model, params string[] channels)
    {
        var buffer = new List<byte>();

        buffer.AddRange(FixedName(oem));    // WCHAR HardwareOEM[16]
        buffer.AddRange(FixedName(model));  // WCHAR HardwareModel[16]
        buffer.AddRange(BitConverter.GetBytes((ushort)1));                 // HardwareRevision
        buffer.AddRange(BitConverter.GetBytes((ushort)channels.Length));   // ChannelCount

        foreach (string channel in channels)
        {
            // EMI_CHANNEL_V2: MeasurementUnit(4) ChannelNameSize(2) ChannelName(가변)
            var name = System.Text.Encoding.Unicode.GetBytes(channel + "\0");

            buffer.AddRange(BitConverter.GetBytes(0));                       // 피코와트시
            buffer.AddRange(BitConverter.GetBytes((ushort)name.Length));
            buffer.AddRange(name);
        }

        return [.. buffer];
    }

    private static byte[] BuildV1(string oem, string model, string meteredHardware)
    {
        var buffer = new List<byte>();
        var name = System.Text.Encoding.Unicode.GetBytes(meteredHardware + "\0");

        buffer.AddRange(BitConverter.GetBytes(0));   // MeasurementUnit
        buffer.AddRange(FixedName(oem));
        buffer.AddRange(FixedName(model));
        buffer.AddRange(BitConverter.GetBytes((ushort)1));                // HardwareRevision
        buffer.AddRange(BitConverter.GetBytes((ushort)name.Length));      // MeteredHardwareNameSize
        buffer.AddRange(name);

        return [.. buffer];
    }

    /// <summary>EMI_NAME_MAX(16) 문자 고정 폭. 남는 자리는 0으로 채운다.</summary>
    private static byte[] FixedName(string value)
    {
        var bytes = new byte[16 * 2];
        var encoded = System.Text.Encoding.Unicode.GetBytes(value);
        Array.Copy(encoded, bytes, Math.Min(encoded.Length, bytes.Length - 2));
        return bytes;
    }

    [Fact]
    public void A_single_channel_v2_meter_is_read()
    {
        var buffer = BuildV2("Microsoft", "PPM", "RAPL_Package0_Core3_CORE");

        var metadata = EnergyMeterReader.ParseMetadata(buffer, 2);

        Assert.NotNull(metadata);
        Assert.Equal("Energy Meter (Microsoft PPM)", metadata.HardwareName);
        Assert.Equal(["RAPL_Package0_Core3_CORE"], metadata.ChannelNames);
    }

    [Fact]
    public void Every_channel_of_a_multi_channel_v2_meter_is_read()
    {
        // 7950X 의 0번 장치가 실제로 이런 모양이다 — 패키지와 0번 코어가 한 장치에 함께 있다.
        var buffer = BuildV2("Microsoft", "PPM", "RAPL_Package0_PKG", "RAPL_Package0_Core0_CORE");

        var metadata = EnergyMeterReader.ParseMetadata(buffer, 2);

        Assert.Equal(["RAPL_Package0_PKG", "RAPL_Package0_Core0_CORE"], metadata!.ChannelNames);
    }

    [Fact]
    public void Channel_names_of_differing_length_do_not_throw_the_walk_off_course()
    {
        // 채널은 4바이트 경계로 맞추지 않는다. 길이를 그대로 더해 넘어가야
        // 홀수 길이 이름 뒤의 채널이 밀리지 않는다.
        var buffer = BuildV2("OEM", "M", "A", "LongerChannelName", "BB");

        var metadata = EnergyMeterReader.ParseMetadata(buffer, 2);

        Assert.Equal(["A", "LongerChannelName", "BB"], metadata!.ChannelNames);
    }

    [Fact]
    public void A_v1_meter_is_read()
    {
        var buffer = BuildV1("Contoso", "Meter", "SYSTEM");

        var metadata = EnergyMeterReader.ParseMetadata(buffer, 1);

        Assert.NotNull(metadata);
        Assert.Equal("Energy Meter (Contoso Meter)", metadata.HardwareName);
        Assert.Equal(["SYSTEM"], metadata.ChannelNames);
    }

    [Fact]
    public void A_meter_that_names_neither_oem_nor_model_still_gets_a_label()
    {
        var buffer = BuildV2("", "", "SYSTEM");

        var metadata = EnergyMeterReader.ParseMetadata(buffer, 2);

        Assert.Equal("Energy Meter", metadata!.HardwareName);
    }

    [Fact]
    public void A_truncated_buffer_is_refused_instead_of_read_past_the_end()
    {
        // 드라이버가 짧은 응답을 주는 일은 실제로 있다.
        // 여기서 예외가 나면 폴링 루프가 통째로 멈춘다.
        var buffer = BuildV2("Microsoft", "PPM", "RAPL_Package0_PKG");

        for (int length = 0; length < buffer.Length; length++)
            Assert.Null(EnergyMeterReader.ParseMetadata(buffer[..length], 2));
    }

    [Fact]
    public void An_absurd_channel_count_is_refused()
    {
        // 버전을 잘못 짚으면 엉뚱한 자리를 채널 수로 읽는다.
        // 그 값으로 반복을 돌기 전에 멈춰야 한다.
        var buffer = BuildV2("Microsoft", "PPM", "RAPL_Package0_PKG");
        BitConverter.GetBytes((ushort)60000).CopyTo(buffer, 66);

        Assert.Null(EnergyMeterReader.ParseMetadata(buffer, 2));
    }

    [Fact]
    public void A_meter_with_no_channels_is_refused()
    {
        var buffer = BuildV2("Microsoft", "PPM", "RAPL_Package0_PKG");
        BitConverter.GetBytes((ushort)0).CopyTo(buffer, 66);

        Assert.Null(EnergyMeterReader.ParseMetadata(buffer, 2));
    }

    [Fact]
    public void Reading_v2_bytes_as_v1_does_not_throw()
    {
        // 버전 조회와 메타데이터 조회가 어긋나는 경우를 대비한다.
        var buffer = BuildV2("Microsoft", "PPM", "RAPL_Package0_PKG");

        var metadata = EnergyMeterReader.ParseMetadata(buffer, 1);

        // 값이 나오든 안 나오든 예외 없이 돌아오기만 하면 된다.
        Assert.True(metadata is null || metadata.ChannelNames.Count == 1);
    }
}
