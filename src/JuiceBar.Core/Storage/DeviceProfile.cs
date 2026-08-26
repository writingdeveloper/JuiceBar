using System.Text.Json;
using System.Text.Json.Serialization;
using JuiceBar.Core.Localization;
using JuiceBar.Core.Platform;
using JuiceBar.Core.Power;
using JuiceBar.Core.Tariff;

namespace JuiceBar.Core.Storage;

/// <summary>트레이 아이콘 게이지가 무엇을 기준으로 차오를지.</summary>
public enum GaugeMode
{
    /// <summary>이번 청구 주기 누적 요금 / 예산. 주유소 연료계에 해당하는 쪽.</summary>
    Budget,

    /// <summary>현재 전력 / 이 장비의 최대 전력. 지금 무엇이 전기를 먹는지 보는 쪽.</summary>
    Instant,
}

/// <summary>
/// 장비 하나에 딸린 모든 설정과 학습 결과.
/// 장비마다 전력 특성이 완전히 다르므로 절대 공유하지 않는다.
/// </summary>
public sealed record DeviceProfile
{
    /// <summary>
    /// 지금 코드가 기대하는 프로필 형식 번호.
    ///
    /// 채널 선택처럼 "첫 실행에 한 번 정하고 그대로 두는" 값이 있다. 나중에 더 나은
    /// 전력 소스가 생겨도 이미 저장된 프로필은 예전 선택을 계속 쓰게 되므로,
    /// 번호를 올려 한 번만 다시 계산하게 한다.
    ///
    ///   1 — Windows 에너지 미터(EMI) 도입. PawnIO 없이도 CPU 를 잴 수 있게 되어
    ///       채널 선택을 다시 정해야 한다.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>이 프로필이 마지막으로 맞춰진 형식 번호. 0 은 에너지 미터 이전이다.</summary>
    public int Version { get; init; }

    public string DeviceId { get; init; } = DeviceIdentity.Current;

    public string DisplayName { get; init; } = DeviceIdentity.FriendlyName;

    public TariffConfig Tariff { get; init; } = new();

    public CalibrationModel Calibration { get; init; } = CalibrationModel.Default;

    /// <summary>CPU 센서를 못 읽을 때 쓰는 추정 파라미터.</summary>
    public EstimationSettings Estimation { get; init; } = new();

    /// <summary>합산에 포함할 전력 채널. 비어 있으면 첫 폴링 때 휴리스틱으로 채운다.</summary>
    public IReadOnlyList<string> SelectedChannelIds { get; init; } = [];

    /// <summary>사용자가 입력한 캘리브레이션 표본. 나중에 점을 더 찍어 정밀도를 올릴 수 있게 보관한다.</summary>
    public IReadOnlyList<CalibrationPoint> CalibrationPoints { get; init; } = [];

    public GaugeMode GaugeMode { get; init; } = GaugeMode.Budget;

    /// <summary>
    /// 관측된 최대 전력. 순간 모드 게이지의 만재 기준이다.
    ///
    /// 0 에서 시작해 실제로 본 값까지만 올라간다. 예전에는 500W 로 시작했는데,
    /// 그러면 65W 짜리 노트북에서 게이지가 영원히 바닥에 붙어 있었다.
    /// </summary>
    public double ObservedPeakWatts { get; init; }

    public bool StartWithWindows { get; init; }

    /// <summary>GitHub 릴리스에서 새 버전을 자동으로 확인할지.</summary>
    public bool CheckForUpdates { get; init; } = true;

    /// <summary>
    /// 화면에 쓸 언어. "auto" 면 Windows 표시 언어를 따라간다.
    /// 장비마다 따로 두는 이유는, 회사 노트북은 영어이고 집 PC는 모국어인 경우가 있어서다.
    /// </summary>
    public string Language { get; init; } = Loc.AutoCode;

    /// <summary>이 장비가 노트북인지. 배터리 자동 캘리브레이션 가능 여부를 뜻한다.</summary>
    public bool HasBattery { get; init; }

    /// <summary>
    /// 사용자가 요금을 실제로 설정했는지. 기본값 그대로면 첫 실행으로 보고
    /// 요금 마법사를 띄운다. 아무 설정 없이 엉뚱한 금액을 보여 주는 것보다 낫다.
    /// </summary>
    public bool IsTariffConfigured { get; init; }

    /// <summary>
    /// 예전 형식으로 저장된 프로필을 지금 형식에 맞춘다. 이미 최신이면 그대로 돌려준다.
    ///
    /// 채널 선택은 첫 실행에 한 번 정해지고 그 뒤로는 손대지 않는다. 그래서 더 나은
    /// 전력 소스가 생겨도 이미 쓰던 사람은 예전 선택에 갇힌다. 여기서 한 번만 풀어 준다.
    /// </summary>
    public static DeviceProfile Migrate(DeviceProfile profile)
    {
        if (profile.Version >= CurrentVersion) return profile;

        var migrated = profile with { Version = CurrentVersion };

        // 전력계로 직접 보정한 사람의 설정은 건드리지 않는다. 합산 대상이 바뀌면
        // 그 사람이 맞춰 놓은 베이스라인·효율이 어긋나기 때문이다.
        // 그런 경우에도 고급 설정에서 채널을 직접 켜면 에너지 미터를 쓸 수 있다.
        if (profile.CalibrationPoints.Count == 0)
            migrated = migrated with { SelectedChannelIds = [] };

        // 예전 기본값 500W 는 관측한 값이 아니라 그냥 자리를 채운 숫자였다.
        // 그 탓에 노트북에서는 순간 게이지가 늘 바닥에 붙어 있었다.
        // 실제로 관측해서 500 이 나왔을 리는 없으니 이 값만 골라 되돌린다.
        if (Math.Abs(profile.ObservedPeakWatts - LegacyDefaultPeakWatts) < 0.001)
            migrated = migrated with { ObservedPeakWatts = 0 };

        return migrated;
    }

    /// <summary>형식 0 에서 <see cref="ObservedPeakWatts"/> 의 기본값이던 숫자.</summary>
    internal const double LegacyDefaultPeakWatts = 500;
}

/// <summary>프로필을 %APPDATA%\JuiceBar\devices\&lt;id&gt;\profile.json 에 읽고 쓴다.</summary>
public sealed class ProfileStore
{
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string DirectoryPath { get; }

    public string FilePath => Path.Combine(DirectoryPath, "profile.json");

    public ProfileStore(string? rootOverride = null)
    {
        string root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "JuiceBar");

        DirectoryPath = Path.Combine(root, "devices", DeviceIdentity.Current);
        Directory.CreateDirectory(DirectoryPath);
    }

    public DeviceProfile Load()
    {
        if (!File.Exists(FilePath)) return new DeviceProfile();

        try
        {
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<DeviceProfile>(json, _json) ?? new DeviceProfile();
        }
        catch (Exception)
        {
            // 손상된 프로필 때문에 앱이 못 뜨는 것이 가장 나쁘다. 기본값으로 살려 둔다.
            return new DeviceProfile();
        }
    }

    public void Save(DeviceProfile profile)
    {
        string json = JsonSerializer.Serialize(profile, _json);

        // 저장 중 전원이 끊겨도 기존 파일이 남도록 임시 파일에 쓰고 교체한다.
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, FilePath, overwrite: true);
    }
}
