namespace JuiceBar.Core.Power;

/// <summary>측정값이 얼마나 믿을 만한지. UI에서 배지로 노출한다.</summary>
public enum PowerQuality
{
    /// <summary>센서를 전혀 못 읽어 사용률 기반으로 추정한 값. 오차가 크다.</summary>
    Estimated,

    /// <summary>센서는 읽었으나 베이스라인·효율이 기본값. 부품 구성에 따라 어긋날 수 있다.</summary>
    SensorUncalibrated,

    /// <summary>센서 + 캘리브레이션 완료. 이 앱이 낼 수 있는 최선.</summary>
    SensorCalibrated,

    /// <summary>배터리 방전율이나 스마트플러그처럼 총 전력을 직접 잰 값.</summary>
    Measured,
}

/// <summary>전력 채널의 성격. 기본 포함 여부를 정하는 휴리스틱에 쓰인다.</summary>
public enum ChannelKind
{
    Cpu,
    Gpu,
    Battery,
    Other,
}

/// <summary>
/// 하드웨어가 보고하는 전력 센서 하나.
/// 어떤 채널을 합산할지는 장비 프로필이 결정한다 — 하드웨어마다 센서 의미가
/// 제각각이라(예: AMD APU의 "GPU Core"가 실제로는 패키지 레일일 수 있다)
/// 코드에 고정하지 않고 사용자가 교정할 수 있게 열어 둔다.
/// </summary>
public sealed record PowerChannel(
    string Id,
    string Label,
    string HardwareName,
    ChannelKind Kind,
    double Watts);

/// <summary>한 번의 폴링 결과.</summary>
public sealed record PowerReading
{
    /// <summary>콘센트 기준 최종 전력. UI가 보여주는 값.</summary>
    public required double WallWatts { get; init; }

    /// <summary>합산에 포함된 센서 값의 총합 (보정 전).</summary>
    public required double MeasuredWatts { get; init; }

    /// <summary>측정 불가 부품에 적용한 상수 전력.</summary>
    public required double BaselineWatts { get; init; }

    public required PowerQuality Quality { get; init; }

    public required IReadOnlyList<PowerChannel> Channels { get; init; }

    /// <summary>노트북이 배터리로 구동 중인지. 자동 캘리브레이션 조건이다.</summary>
    public required bool OnBattery { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// 합산에 포함된 CPU 채널의 합.
    ///
    /// <see cref="Channels"/> 를 그냥 훑어서는 안 된다 — 그 목록에는 고르지 않은 채널도
    /// 들어 있어서(코어별 센서, 쓰지 않는 쪽의 CPU 소스) 같은 전력이 여러 번 잡힌다.
    /// </summary>
    public required double CpuWatts { get; init; }

    /// <summary>합산에 포함된 GPU 채널의 합.</summary>
    public required double GpuWatts { get; init; }

    /// <summary>
    /// CPU 도 GPU 도 아닌 몫. 메인보드·RAM·SSD·팬과 파워서플라이 손실이 여기 들어간다.
    /// 잴 수 없어서 모델로 메우는 부분이므로 따로 떼어 보여 준다.
    /// </summary>
    public double OtherWatts => Math.Max(0, WallWatts - CpuWatts - GpuWatts);
}
