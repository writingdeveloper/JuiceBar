using JuiceBar.Core.Localization;
using JuiceBar.Core.Power;

namespace JuiceBar.Tests;

[Collection(LocalizationCollection.Name)]
public class CalibrationModelTests
{
    /// <summary>알려진 B, η 로 합성한 표본에서 원래 값이 복원되어야 한다.</summary>
    [Theory]
    [InlineData(35.0, 0.88)]
    [InlineData(50.0, 0.92)]
    [InlineData(12.5, 0.75)]
    public void Fit_recovers_the_parameters_used_to_synthesise_the_samples(
        double baseline, double efficiency)
    {
        var truth = new CalibrationModel
        {
            BaselineWatts = baseline,
            Efficiency = efficiency,
            IsCalibrated = true,
            SampleCount = 0,
        };

        // 유휴와 고부하에 해당하는 센서 합산값 두 개를 고르고,
        // 모델을 거꾸로 돌려 "실측되었을" 콘센트 전력을 만든다.
        var points = new[]
        {
            SynthesisePoint(truth, measuredWatts: 60),
            SynthesisePoint(truth, measuredWatts: 350),
        };

        var result = CalibrationModel.Fit(points);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Model);
        Assert.Equal(baseline, result.Model.BaselineWatts, precision: 6);
        Assert.Equal(efficiency, result.Model.Efficiency, precision: 6);
        Assert.True(result.Model.IsCalibrated);
    }

    private static CalibrationPoint SynthesisePoint(CalibrationModel truth, double measuredWatts)
        => new(measuredWatts, truth.ToWallWatts(measuredWatts));

    /// <summary>테스트가 도는 기계의 시스템 언어에 좌우되지 않게 못 박는다.</summary>
    private static void UseEnglish() => Loc.Use("en");

    [Fact]
    public void Fit_needs_at_least_two_samples()
    {
        UseEnglish();
        var result = CalibrationModel.Fit([new CalibrationPoint(60, 108)]);

        Assert.False(result.Success);
        Assert.Contains("two readings", result.Error);
    }

    [Fact]
    public void Fit_rejects_samples_taken_at_effectively_the_same_load()
    {
        UseEnglish();
        // 두 점의 콘센트 전력이 같으면 직선의 기울기를 정할 수 없다.
        var result = CalibrationModel.Fit(
        [
            new CalibrationPoint(60, 108),
            new CalibrationPoint(61, 108),
        ]);

        Assert.False(result.Success);
        Assert.Contains("too close together", result.Error);
    }

    [Fact]
    public void Fit_rejects_a_physically_impossible_efficiency()
    {
        UseEnglish();
        // 센서 합산이 콘센트 전력보다 크게 나오면 효율이 1을 넘어 버린다.
        var result = CalibrationModel.Fit(
        [
            new CalibrationPoint(100, 80),
            new CalibrationPoint(400, 300),
        ]);

        Assert.False(result.Success);
        Assert.Contains("efficiency", result.Error);
    }

    [Fact]
    public void Fit_rejects_an_implausible_baseline()
    {
        UseEnglish();
        // 효율은 0.9 로 정상 범위지만 베이스라인이 400W 로 나온다 —
        // 콘센트 전력에 비해 센서가 잡아내는 몫이 지나치게 작은 경우다.
        var result = CalibrationModel.Fit(
        [
            new CalibrationPoint(50, 500),
            new CalibrationPoint(320, 800),
        ]);

        Assert.False(result.Success);
        Assert.Contains("baseline", result.Error);
    }

    [Fact]
    public void Default_model_is_marked_uncalibrated()
    {
        Assert.False(CalibrationModel.Default.IsCalibrated);
    }

    [Fact]
    public void Conversion_adds_the_baseline_then_divides_by_efficiency()
    {
        var model = new CalibrationModel
        {
            BaselineWatts = 40,
            Efficiency = 0.8,
            IsCalibrated = true,
            SampleCount = 2,
        };

        Assert.Equal(200, model.ToWallWatts(120), precision: 6);
    }

    [Fact]
    public void Battery_learning_adopts_the_first_observation_directly()
    {
        var learned = CalibrationModel.Default
            .LearnBaselineFromDischarge(measuredWatts: 12, actualDcWatts: 20);

        Assert.True(learned.IsCalibrated);
        Assert.Equal(8, learned.BaselineWatts, precision: 6);
    }

    [Fact]
    public void Battery_learning_converges_towards_the_observed_baseline()
    {
        var model = CalibrationModel.Default.LearnBaselineFromDischarge(12, 20);

        for (int i = 0; i < 2000; i++)
            model = model.LearnBaselineFromDischarge(measuredWatts: 12, actualDcWatts: 25);

        Assert.Equal(13, model.BaselineWatts, precision: 3);
    }

    [Fact]
    public void Battery_learning_ignores_impossible_observations()
    {
        var model = CalibrationModel.Default.LearnBaselineFromDischarge(12, 20);
        var before = model.BaselineWatts;

        // 센서 합이 총 방전량보다 크다 — 채널 선택이 잘못된 상황이므로 학습하면 안 된다.
        var after = model.LearnBaselineFromDischarge(measuredWatts: 50, actualDcWatts: 20);

        Assert.Equal(before, after.BaselineWatts, precision: 6);
    }
}
