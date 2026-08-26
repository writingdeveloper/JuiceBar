using JuiceBar.Core.Power;

namespace JuiceBar.Tests;

/// <summary>
/// "CPU 를 실측하려면 지금 무엇이 필요한가"를 정하는 판단.
///
/// 이게 틀리면 사용자에게 엉뚱한 것을 시킨다 — 이미 잘 되고 있는 사람에게 커널
/// 드라이버를 깔라고 하거나, 깔아 둔 사람에게 또 깔라고 하거나, 권한만 올리면
/// 되는데 설치 페이지로 보내거나.
/// </summary>
public class SensorAccessTests
{
    [Theory]
    // 에너지 미터가 있으면 나머지는 볼 것도 없다. 드라이버도 승격도 필요 없다.
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void An_energy_meter_settles_it_regardless_of_anything_else(
        bool meter, bool driver, bool elevated)
    {
        Assert.Equal(SensorAdvice.Measuring, SensorAccess.Advise(meter, driver, elevated));
    }

    [Fact]
    public void Without_a_meter_or_a_driver_the_driver_is_what_is_missing()
    {
        Assert.Equal(SensorAdvice.InstallDriver, SensorAccess.Advise(false, false, false));
    }

    [Fact]
    public void Being_elevated_does_not_help_when_the_driver_is_absent()
    {
        // 승격만으로는 MSR 을 읽을 수 없다. 커널 쪽 드라이버가 있어야 한다.
        // 여기서 "이미 관리자니까 됐다"고 판단하면 CPU 가 영영 0W 로 남는다.
        Assert.Equal(SensorAdvice.InstallDriver, SensorAccess.Advise(false, driverInstalled: false, isElevated: true));
    }

    [Fact]
    public void With_the_driver_installed_only_elevation_is_missing()
    {
        Assert.Equal(SensorAdvice.RunElevated, SensorAccess.Advise(false, driverInstalled: true, isElevated: false));
    }

    [Fact]
    public void Driver_plus_elevation_measures()
    {
        Assert.Equal(SensorAdvice.Measuring, SensorAccess.Advise(false, driverInstalled: true, isElevated: true));
    }

    [Theory]
    [InlineData(SensorAdvice.Measuring, false)]
    [InlineData(SensorAdvice.InstallDriver, true)]
    [InlineData(SensorAdvice.RunElevated, true)]
    public void Only_a_shortfall_is_worth_interrupting_someone_for(SensorAdvice advice, bool expected)
    {
        Assert.Equal(expected, SensorAccess.NeedsAttention(advice));
    }

    [Fact]
    public void The_common_windows_11_case_never_asks_for_anything()
    {
        // Windows 11 은 CPU 의 RAPL 값을 에너지 미터로 그대로 내보낸다.
        // 그러니 드라이버도 없고 승격도 안 된 평범한 실행이 곧 최선의 상태다.
        var advice = SensorAccess.Advise(hasEnergyMeter: true, driverInstalled: false, isElevated: false);

        Assert.Equal(SensorAdvice.Measuring, advice);
        Assert.False(SensorAccess.NeedsAttention(advice));
    }
}
