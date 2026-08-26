namespace JuiceBar.Core.Power;

/// <summary>
/// CPU 전력 센서를 못 읽을 때(PawnIO 미설치) 쓰는 추정 파라미터.
///
/// GPU는 NVML·ADL로 권한 없이도 실측되므로 전체를 추정으로 떨어뜨릴 필요는 없다.
/// CPU 몫만 사용률로 메우면 총합의 정확도를 상당 부분 지킬 수 있다.
/// </summary>
public sealed record EstimationSettings
{
    /// <summary>사용률 0%일 때의 CPU 패키지 전력. Zen 4 데스크톱은 IOD 때문에 유휴에도 높다.</summary>
    public double CpuIdleWatts { get; init; } = 40;

    /// <summary>사용률 100%일 때의 CPU 패키지 전력. 대략 PPT 한계에 해당한다.</summary>
    public double CpuMaxWatts { get; init; } = 200;

    /// <summary>
    /// 사용률-전력 곡선의 휘어짐. 1보다 크면 낮은 사용률 구간에서 완만하다 —
    /// 실제 CPU는 부스트 때문에 선형이 아니라 이렇게 움직인다.
    /// </summary>
    public double CurveExponent { get; init; } = 1.3;

    public double EstimateCpuWatts(double loadPercent)
    {
        double load = Math.Clamp(loadPercent, 0, 100) / 100.0;
        double span = Math.Max(0, CpuMaxWatts - CpuIdleWatts);

        return CpuIdleWatts + (span * Math.Pow(load, CurveExponent));
    }
}
