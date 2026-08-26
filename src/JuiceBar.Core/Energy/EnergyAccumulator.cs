namespace JuiceBar.Core.Energy;

/// <summary>
/// 전력(W) 표본을 에너지(Wh)로 적분한다.
///
/// 사다리꼴 적분을 쓴다 — 표본 사이에서 전력이 선형으로 변한다고 보는 편이
/// 직전 값을 그대로 유지한다고 보는 것보다 실제에 가깝다.
///
/// 절전 모드 복귀나 시스템 시계 변경으로 표본 간격이 비정상적으로 벌어지면
/// 그 구간은 적분하지 않는다. 잠들어 있던 시간을 고부하로 오인해 요금을
/// 부풀리는 것보다, 소량을 누락하는 쪽이 낫다.
/// </summary>
public sealed class EnergyAccumulator
{
    /// <summary>1초 폴링에서 이보다 벌어지면 정상적인 표본 간격이 아니다.</summary>
    public static readonly TimeSpan DefaultMaxSampleGap = TimeSpan.FromSeconds(30);

    private double? _previousWatts;

    /// <summary>이보다 긴 간격은 절전이나 시계 변경으로 보고 버린다.</summary>
    public TimeSpan MaxSampleGap { get; }

    public EnergyAccumulator(TimeSpan? maxSampleGap = null)
        => MaxSampleGap = maxSampleGap ?? DefaultMaxSampleGap;

    public double TotalWattHours { get; private set; }

    public double TotalKilowattHours => TotalWattHours / 1000.0;

    /// <summary>비정상 간격으로 버린 구간의 수. 진단용.</summary>
    public int SkippedGaps { get; private set; }

    /// <summary>표본을 추가하고, 이번에 누적된 Wh를 돌려준다.</summary>
    public double Add(double watts, TimeSpan elapsed)
    {
        if (_previousWatts is not double previous)
        {
            // 첫 표본은 적분할 구간이 없다. 기준점으로만 삼는다.
            _previousWatts = watts;
            return 0;
        }

        if (elapsed <= TimeSpan.Zero)
            return 0;

        if (elapsed > MaxSampleGap)
        {
            SkippedGaps++;
            _previousWatts = watts;
            return 0;
        }

        double deltaWattHours = (previous + watts) / 2.0 * elapsed.TotalHours;

        _previousWatts = watts;
        TotalWattHours += deltaWattHours;

        return deltaWattHours;
    }

    /// <summary>저장된 누적값에서 이어서 시작할 때 쓴다.</summary>
    public void Restore(double totalWattHours) => TotalWattHours = totalWattHours;

    /// <summary>청구 주기가 바뀔 때 호출한다.</summary>
    public void Reset()
    {
        TotalWattHours = 0;
        _previousWatts = null;
        SkippedGaps = 0;
    }
}
