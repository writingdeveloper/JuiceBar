using JuiceBar.Core.Power;
using JuiceBar.Core.Storage;

namespace JuiceBar.Tests;

/// <summary>
/// 이미 쓰고 있는 사람의 프로필을 새 형식으로 옮기는 부분.
///
/// 여기가 지나치면 사용자가 맞춰 놓은 설정을 말없이 뒤엎게 되고,
/// 모자라면 새 기능이 기존 사용자에게 영영 닿지 않는다.
/// </summary>
public class ProfileMigrationTests
{
    [Fact]
    public void A_fresh_profile_is_already_current()
    {
        var profile = new DeviceProfile();

        Assert.Equal(DeviceProfile.CurrentVersion, DeviceProfile.Migrate(profile).Version);
    }

    [Fact]
    public void An_already_migrated_profile_is_returned_untouched()
    {
        var profile = new DeviceProfile
        {
            Version = DeviceProfile.CurrentVersion,
            SelectedChannelIds = ["/amdcpu/0/power/16"],
            ObservedPeakWatts = DeviceProfile.LegacyDefaultPeakWatts,
        };

        // 같은 객체를 그대로 돌려줘야 호출하는 쪽이 "바뀐 게 없다"를 값싸게 판단한다.
        Assert.Same(profile, DeviceProfile.Migrate(profile));
    }

    [Fact]
    public void The_channel_choice_is_recomputed_so_the_energy_meter_can_be_picked_up()
    {
        // 채널 선택은 첫 실행에 한 번만 정해진다. 비워 두면 다음 폴링에서 다시 고른다.
        var profile = new DeviceProfile
        {
            SelectedChannelIds = ["/amdcpu/0/power/16", "/gpu-nvidia/0/power/0"],
        };

        Assert.Empty(DeviceProfile.Migrate(profile).SelectedChannelIds);
    }

    [Fact]
    public void A_calibrated_profile_keeps_its_channels()
    {
        // 전력계로 직접 잰 사람은 그 채널 조합에 맞춰 베이스라인과 효율을 얻었다.
        // 합산 대상을 바꾸면 그 값이 통째로 어긋나므로 손대지 않는다.
        var profile = new DeviceProfile
        {
            SelectedChannelIds = ["/amdcpu/0/power/16"],
            CalibrationPoints = [new CalibrationPoint(62.67, 120)],
        };

        var migrated = DeviceProfile.Migrate(profile);

        Assert.Equal(["/amdcpu/0/power/16"], migrated.SelectedChannelIds);
        Assert.Equal(DeviceProfile.CurrentVersion, migrated.Version);
    }

    [Fact]
    public void The_old_placeholder_peak_is_cleared()
    {
        // 500W 는 관측값이 아니라 자리를 채운 숫자였다.
        // 그대로 두면 65W 짜리 노트북에서 순간 게이지가 영영 바닥에 붙어 있다.
        var profile = new DeviceProfile { ObservedPeakWatts = DeviceProfile.LegacyDefaultPeakWatts };

        Assert.Equal(0, DeviceProfile.Migrate(profile).ObservedPeakWatts);
    }

    [Fact]
    public void A_genuinely_observed_peak_survives()
    {
        var profile = new DeviceProfile { ObservedPeakWatts = 612.4 };

        Assert.Equal(612.4, DeviceProfile.Migrate(profile).ObservedPeakWatts);
    }

    [Fact]
    public void Everything_else_is_carried_over_unchanged()
    {
        var profile = new DeviceProfile
        {
            DisplayName = "작업실 PC",
            GaugeMode = GaugeMode.Instant,
            Language = "ja",
            IsTariffConfigured = true,
            CheckForUpdates = false,
            HasBattery = true,
        };

        var migrated = DeviceProfile.Migrate(profile);

        Assert.Equal("작업실 PC", migrated.DisplayName);
        Assert.Equal(GaugeMode.Instant, migrated.GaugeMode);
        Assert.Equal("ja", migrated.Language);
        Assert.True(migrated.IsTariffConfigured);
        Assert.False(migrated.CheckForUpdates);
        Assert.True(migrated.HasBattery);
    }

    [Fact]
    public void Migrating_twice_changes_nothing_the_second_time()
    {
        var profile = new DeviceProfile
        {
            SelectedChannelIds = ["/amdcpu/0/power/16"],
            ObservedPeakWatts = DeviceProfile.LegacyDefaultPeakWatts,
        };

        var once = DeviceProfile.Migrate(profile);
        var twice = DeviceProfile.Migrate(once);

        Assert.Same(once, twice);
    }
}
