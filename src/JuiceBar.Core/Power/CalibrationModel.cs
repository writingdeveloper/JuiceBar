namespace JuiceBar.Core.Power;

/// <summary>캘리브레이션 표본 하나 — 센서 합산값과 그때 실제로 측정된 콘센트 전력.</summary>
public readonly record struct CalibrationPoint(double MeasuredWatts, double ActualWallWatts);

/// <summary>
/// 센서로 잰 값을 콘센트 기준 전력으로 옮기는 2파라미터 모델.
///
///     P_wall = (P_measured + B) / η
///
/// B는 측정할 수 없는 부품(메인보드·RAM·SSD·팬)의 상수 전력, η는 파워서플라이 효율이다.
/// 둘 다 하드웨어마다 다르므로 실측 표본에서 역산한다.
/// </summary>
public sealed record CalibrationModel
{
    /// <summary>측정 기기가 없을 때 쓰는 값. 일반적인 ATX 데스크톱 기준의 보수적 추정.</summary>
    public static readonly CalibrationModel Default = new()
    {
        BaselineWatts = 35.0,
        Efficiency = 0.88,
        IsCalibrated = false,
        SampleCount = 0,
    };

    public required double BaselineWatts { get; init; }

    public required double Efficiency { get; init; }

    /// <summary>실측 표본으로 적합한 값인지. false면 UI에 "미보정" 배지를 띄운다.</summary>
    public required bool IsCalibrated { get; init; }

    public required int SampleCount { get; init; }

    /// <summary>센서 합산값을 콘센트 기준으로 환산한다.</summary>
    public double ToWallWatts(double measuredWatts)
        => (measuredWatts + BaselineWatts) / Efficiency;

    /// <summary>
    /// 표본들에 최소제곱으로 적합한다.
    ///
    /// P_measured = η · P_wall − B 형태의 직선이므로, (P_wall, P_measured) 점들에
    /// 회귀선을 그으면 기울기가 η, y절편이 −B가 된다.
    /// </summary>
    public static CalibrationResult Fit(IReadOnlyList<CalibrationPoint> points)
    {
        if (points.Count < 2)
            return CalibrationResult.Failed("표본이 2개 이상 필요합니다.");

        double n = points.Count;
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;

        foreach (var p in points)
        {
            sumX += p.ActualWallWatts;
            sumY += p.MeasuredWatts;
            sumXY += p.ActualWallWatts * p.MeasuredWatts;
            sumXX += p.ActualWallWatts * p.ActualWallWatts;
        }

        double denominator = (n * sumXX) - (sumX * sumX);

        // 두 표본의 콘센트 전력이 거의 같으면 직선을 정할 수 없다.
        // 유휴와 부하처럼 충분히 떨어진 지점에서 재야 한다.
        if (Math.Abs(denominator) < 1e-6)
            return CalibrationResult.Failed("표본들의 전력 차이가 너무 작습니다. 유휴 상태와 고부하 상태에서 각각 측정해 주세요.");

        double slope = ((n * sumXY) - (sumX * sumY)) / denominator;
        double intercept = (sumY - (slope * sumX)) / n;

        double efficiency = slope;
        double baseline = -intercept;

        // 물리적으로 말이 되는 범위를 벗어나면 잘못 잰 것이다.
        if (efficiency is <= 0.5 or > 1.0)
            return CalibrationResult.Failed(
                $"계산된 파워서플라이 효율이 {efficiency:P0}로 비정상입니다. 측정값을 다시 확인해 주세요.");

        if (baseline is < 0 or > 300)
            return CalibrationResult.Failed(
                $"계산된 베이스라인이 {baseline:F0}W로 비정상입니다. 측정값을 다시 확인해 주세요.");

        return CalibrationResult.Succeeded(new CalibrationModel
        {
            BaselineWatts = baseline,
            Efficiency = efficiency,
            IsCalibrated = true,
            SampleCount = points.Count,
        });
    }

    /// <summary>
    /// 노트북 전용. 배터리 방전 중에는 방전율이 곧 시스템 전체 DC 전력이므로
    /// η 없이 B만 학습할 수 있다. 사용자 입력이 전혀 필요 없는 경로다.
    ///
    /// 표본이 순간적으로 튀는 일이 잦아 지수이동평균으로 완만하게 수렴시킨다.
    /// </summary>
    public CalibrationModel LearnBaselineFromDischarge(
        double measuredWatts,
        double actualDcWatts,
        double smoothing = 0.02)
    {
        double observedBaseline = actualDcWatts - measuredWatts;

        // 센서가 총량을 넘게 보고했다면 채널 선택이 잘못된 것이다. 학습하지 않는다.
        if (observedBaseline < 0 || observedBaseline > 300)
            return this;

        double updated = IsCalibrated
            ? (BaselineWatts * (1 - smoothing)) + (observedBaseline * smoothing)
            : observedBaseline;

        return this with
        {
            BaselineWatts = updated,
            IsCalibrated = true,
            SampleCount = SampleCount + 1,
        };
    }
}

public sealed record CalibrationResult(bool Success, CalibrationModel? Model, string? Error)
{
    public static CalibrationResult Succeeded(CalibrationModel model) => new(true, model, null);
    public static CalibrationResult Failed(string error) => new(false, null, error);
}
