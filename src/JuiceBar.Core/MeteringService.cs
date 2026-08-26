using System.Diagnostics;
using JuiceBar.Core.Energy;
using JuiceBar.Core.Localization;
using JuiceBar.Core.Power;
using JuiceBar.Core.Storage;
using JuiceBar.Core.Tariff;

namespace JuiceBar.Core;

/// <summary>UI가 한 번에 그릴 수 있도록 계산을 끝낸 상태 한 벌.</summary>
public sealed record MeteringSnapshot
{
    public required PowerReading Power { get; init; }

    /// <summary>이번 청구 주기 누적 사용량.</summary>
    public required double CycleKwh { get; init; }

    public required CostBreakdown CycleCost { get; init; }

    public required double TodayKwh { get; init; }

    public required double TodayCost { get; init; }

    /// <summary>지금 추세가 이어질 때의 주기 말 예상 요금.</summary>
    public required double ProjectedCycleCost { get; init; }

    /// <summary>현재 전력으로 시간당 붙는 요금. UI가 이 값으로 숫자를 흘려 보인다.</summary>
    public required double CostPerHour { get; init; }

    /// <summary>요금이 설정되어 있는지. 첫 실행에서 마법사를 띄울지 판단한다.</summary>
    public required bool IsTariffConfigured { get; init; }

    public TierWarning? TierWarning { get; init; }

    /// <summary>게이지 채움 비율 0~1. 모드에 따라 의미가 다르다.</summary>
    public required double GaugeFraction { get; init; }

    public required GaugeMode GaugeMode { get; init; }

    public required TariffConfig Tariff { get; init; }
}

/// <summary>
/// 센서 폴링 → 보정 → 적분 → 요금 계산을 잇는 중심 서비스.
///
/// 1초에 한 번 돌며 UI에 스냅샷을 던지고, 1분에 한 번 이력 DB에 기록한다.
/// 폴링은 백그라운드 스레드에서 일어나므로 UI 쪽에서 마셜링해야 한다.
/// </summary>
public sealed class MeteringService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly SensorReader _reader;
    private readonly HistoryDatabase _history;
    private readonly ProfileStore _profileStore;
    private readonly EnergyAccumulator _accumulator = new();
    private readonly Lock _gate = new();

    private System.Threading.Timer? _timer;
    private long _lastTimestamp;
    private DateTimeOffset _currentMinute;
    private double _minuteWattHours;
    private double _minuteWattSum;
    private int _minuteSampleCount;
    private bool _disposed;

    public DeviceProfile Profile { get; private set; }

    public MeteringSnapshot? Latest { get; private set; }

    public event EventHandler<MeteringSnapshot>? Updated;

    public MeteringService(ProfileStore profileStore)
    {
        _profileStore = profileStore;
        Profile = profileStore.Load();

        _reader = new SensorReader();
        _history = new HistoryDatabase(Path.Combine(profileStore.DirectoryPath, "history.sqlite"));

        _currentMinute = FloorToMinute(DateTimeOffset.UtcNow);

        MigrateProfile();
    }

    /// <summary>Windows 에너지 미터로 CPU 전력을 읽고 있는지. PawnIO 안내를 띄울지 판단한다.</summary>
    public bool HasEnergyMeter => _reader.HasEnergyMeter;

    /// <summary>저장된 프로필이 예전 형식이면 지금 형식으로 옮기고 바로 저장한다.</summary>
    private void MigrateProfile()
    {
        var migrated = DeviceProfile.Migrate(Profile);
        if (ReferenceEquals(migrated, Profile)) return;

        Profile = migrated;
        _profileStore.Save(migrated);
    }

    /// <summary>
    /// 이번 청구 주기의 누적을 지운다.
    ///
    /// 요금제를 바꿨거나, 앱을 시험하느라 쌓인 값이 실제 사용량과 어긋났을 때 쓴다.
    /// 지운 사용량(kWh)을 돌려준다.
    /// </summary>
    public double ResetCurrentCycle(DateTimeOffset? asOf = null)
    {
        lock (_gate)
        {
            var now = asOf ?? DateTimeOffset.UtcNow;
            var (cycleStart, _) = BillingCycle.Current(Profile.Tariff.BillingCycleStartDay, now);

            // 아직 DB 로 내려가지 않은 현재 분의 몫까지 합쳐서 알려 준다.
            double removedWattHours = _history.DeleteRange(cycleStart.ToUniversalTime(), now.AddMinutes(1))
                + _minuteWattHours;

            _minuteWattHours = 0;
            _minuteWattSum = 0;
            _minuteSampleCount = 0;
            _accumulator.Reset();

            // 캐시가 남아 있으면 다음 1초 동안 지운 값이 그대로 다시 보인다.
            _totals = null;

            // 순간 모드 게이지의 만재 기준도 함께 다시 배우게 한다.
            Profile = Profile with { ObservedPeakWatts = 0 };
            _profileStore.Save(Profile);

            return removedWattHours / 1000.0;
        }
    }

    public void Start()
    {
        _lastTimestamp = Stopwatch.GetTimestamp();
        _timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.Zero, PollInterval);
    }

    public void UpdateProfile(DeviceProfile profile)
    {
        lock (_gate)
        {
            Profile = profile;
            _profileStore.Save(profile);
        }
    }

    /// <summary>
    /// 지금 이 순간의 센서 합산값과 사용자가 전력계로 잰 실제 콘센트 전력을 한 쌍으로 기록하고
    /// 다시 적합한다. 유휴와 고부하 두 지점에서 부르면 캘리브레이션이 완성된다.
    /// </summary>
    public CalibrationResult AddCalibrationPoint(double actualWallWatts)
    {
        lock (_gate)
        {
            if (Latest is null)
                return CalibrationResult.Failed(Loc.T("calib.notReady"));

            var points = new List<CalibrationPoint>(Profile.CalibrationPoints)
            {
                new(Latest.Power.MeasuredWatts, actualWallWatts),
            };

            var result = CalibrationModel.Fit(points);

            // 적합에 실패해도 표본은 남긴다. 다음 점을 찍으면 성공할 수 있다.
            var updated = Profile with { CalibrationPoints = points };
            if (result is { Success: true, Model: not null })
                updated = updated with { Calibration = result.Model };

            Profile = updated;
            _profileStore.Save(updated);

            return result;
        }
    }

    public void ResetCalibration()
    {
        lock (_gate)
        {
            Profile = Profile with
            {
                CalibrationPoints = [],
                Calibration = CalibrationModel.Default,
            };
            _profileStore.Save(Profile);
        }
    }

    public IReadOnlyList<MinuteSample> RecentHistory(int minutes)
    {
        lock (_gate) return _history.RecentMinutes(minutes);
    }

    private void Poll()
    {
        // 폴링이 느려져 다음 틱과 겹치면 그냥 건너뛴다. 밀린 작업을 쌓아 봐야 값이 왜곡될 뿐이다.
        if (!_gate.TryEnter()) return;

        try
        {
            var now = DateTimeOffset.UtcNow;
            long timestamp = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_lastTimestamp, timestamp);
            _lastTimestamp = timestamp;

            var sensors = _reader.Read();
            var reading = BuildReading(sensors, now);

            AccumulateEnergy(reading, elapsed, now);

            var snapshot = BuildSnapshot(reading, now);
            Latest = snapshot;

            Updated?.Invoke(this, snapshot);
        }
        catch (Exception)
        {
            // 센서 한 번 못 읽었다고 상주 앱이 죽으면 안 된다. 다음 틱에 다시 시도한다.
        }
        finally
        {
            _gate.Exit();
        }
    }

    private PowerReading BuildReading(SensorSnapshot sensors, DateTimeOffset now)
    {
        var selection = ResolveSelection(sensors.Channels);

        double measured = 0;
        double cpuMeasured = 0;
        double gpuMeasured = 0;

        foreach (var channel in sensors.Channels)
        {
            if (!selection.Contains(channel.Id)) continue;

            measured += channel.Watts;

            if (channel.Kind == ChannelKind.Cpu) cpuMeasured += channel.Watts;
            else if (channel.Kind == ChannelKind.Gpu) gpuMeasured += channel.Watts;
        }

        var quality = Profile.Calibration.IsCalibrated
            ? PowerQuality.SensorCalibrated
            : PowerQuality.SensorUncalibrated;

        // CPU 센서가 0이면 PawnIO가 없다는 뜻이다. GPU 실측은 살아 있으므로
        // CPU 몫만 사용률로 메우고, 품질 등급만 낮춘다.
        if (cpuMeasured < 0.5 && sensors.CpuLoadPercent > 0)
        {
            double estimated = Profile.Estimation.EstimateCpuWatts(sensors.CpuLoadPercent);

            measured += estimated;
            cpuMeasured = estimated;
            quality = PowerQuality.Estimated;
        }

        double baseline = Profile.Calibration.BaselineWatts;
        double wallWatts;

        if (sensors.Battery.OnBattery && sensors.Battery.DischargeWatts > 0)
        {
            // 배터리 구동 중에는 방전율이 곧 시스템 전체 전력이다. 보정이 필요 없다.
            wallWatts = sensors.Battery.DischargeWatts;
            quality = PowerQuality.Measured;

            LearnFromBattery(measured, sensors.Battery.DischargeWatts);
        }
        else
        {
            wallWatts = Profile.Calibration.ToWallWatts(measured);

            // 충전 중이라면 그 전력도 콘센트에서 나온다.
            if (sensors.Battery.ChargeWatts > 0)
                wallWatts += sensors.Battery.ChargeWatts / Profile.Calibration.Efficiency;
        }

        return new PowerReading
        {
            WallWatts = wallWatts,
            MeasuredWatts = measured,
            BaselineWatts = baseline,
            Quality = quality,
            CpuWatts = cpuMeasured,
            GpuWatts = gpuMeasured,
            Channels = sensors.Channels,
            OnBattery = sensors.Battery.OnBattery,
            Timestamp = now,
        };
    }

    /// <summary>
    /// 프로필에 채널 선택이 없으면 휴리스틱으로 채우고 저장한다.
    /// 첫 실행에서 한 번만 일어난다.
    /// </summary>
    private HashSet<string> ResolveSelection(IReadOnlyList<PowerChannel> channels)
    {
        if (Profile.SelectedChannelIds.Count > 0)
            return [.. Profile.SelectedChannelIds];

        var defaults = SensorReader.DefaultSelection(channels);
        if (defaults.Count == 0) return defaults;

        Profile = Profile with
        {
            SelectedChannelIds = [.. defaults],
            HasBattery = channels.Any(c => c.Kind == ChannelKind.Battery),
        };
        _profileStore.Save(Profile);

        return defaults;
    }

    private void LearnFromBattery(double measuredWatts, double actualDcWatts)
    {
        var learned = Profile.Calibration.LearnBaselineFromDischarge(measuredWatts, actualDcWatts);
        if (ReferenceEquals(learned, Profile.Calibration)) return;

        Profile = Profile with { Calibration = learned };

        // 매초 디스크에 쓰지 않는다. 표본 300개(약 5분)마다 한 번이면 충분하다.
        if (learned.SampleCount % 300 == 0)
            _profileStore.Save(Profile);
    }

    private void AccumulateEnergy(PowerReading reading, TimeSpan elapsed, DateTimeOffset now)
    {
        double deltaWattHours = _accumulator.Add(reading.WallWatts, elapsed);

        _minuteWattHours += deltaWattHours;
        _minuteWattSum += reading.WallWatts;
        _minuteSampleCount++;

        // 센서가 한 번 튀었다고 만재 기준이 영구히 망가지면 안 된다.
        if (reading.WallWatts > Profile.ObservedPeakWatts && reading.WallWatts < MaxCredibleWatts)
            Profile = Profile with { ObservedPeakWatts = reading.WallWatts };

        var minute = FloorToMinute(now);
        if (minute <= _currentMinute) return;

        FlushMinute();
        _currentMinute = minute;
    }

    private void FlushMinute()
    {
        if (_minuteSampleCount == 0) return;

        string? period = Profile.Tariff.Rate is TouRate tou
            ? TariffCalculator.ResolvePeriod(tou, _currentMinute.ToLocalTime()).Name
            : null;

        _history.RecordMinute(
            _currentMinute,
            _minuteWattHours,
            _minuteWattSum / _minuteSampleCount,
            period);

        _minuteWattHours = 0;
        _minuteWattSum = 0;
        _minuteSampleCount = 0;
    }

    /// <summary>
    /// DB 집계 결과를 분 단위로 캐시한다.
    ///
    /// 이력이 한 달이면 4만 행이 넘는데, 그걸 매초 세 번씩 훑을 이유가 없다.
    /// 분이 넘어갈 때만 다시 집계하고, 그 사이는 아직 기록되지 않은 현재 분의 몫만 더한다.
    /// 값이 매초 매끄럽게 움직이는 건 그대로다.
    /// </summary>
    private sealed record HistoryTotals(
        DateTimeOffset Minute,
        DateTimeOffset CycleStart,
        double CycleWattHours,
        double TodayWattHours,
        Dictionary<string, double>? CycleByPeriod);

    private HistoryTotals? _totals;

    private HistoryTotals ReadTotals(DateTimeOffset cycleStart, DateTimeOffset now, bool needPeriods)
    {
        var minute = FloorToMinute(now);

        // 같은 분 안에서는 DB 를 다시 읽지 않는다.
        // 청구 주기가 바뀌었으면 분이 그대로여도 다시 읽어야 한다 — 누적이 0부터 시작해야 하므로.
        bool usable = _totals is { } cached
            && cached.Minute == minute
            && cached.CycleStart == cycleStart
            && (!needPeriods || cached.CycleByPeriod is not null);

        if (usable) return _totals!;

        var cycleStartUtc = cycleStart.ToUniversalTime();
        var todayStartUtc = new DateTimeOffset(now.ToLocalTime().Date, now.Offset).ToUniversalTime();

        _totals = new HistoryTotals(
            minute,
            cycleStart,
            _history.SumWattHours(cycleStartUtc, now),
            _history.SumWattHours(todayStartUtc, now),
            needPeriods ? _history.SumWattHoursByPeriod(cycleStartUtc, now) : null);

        return _totals;
    }

    private MeteringSnapshot BuildSnapshot(PowerReading reading, DateTimeOffset now)
    {
        var tariff = Profile.Tariff;
        var (cycleStart, _) = BillingCycle.Current(tariff.BillingCycleStartDay, now);

        bool timeOfUse = tariff.Rate is TouRate;
        var totals = ReadTotals(cycleStart, now, timeOfUse);

        // DB에 아직 안 내려간 현재 분의 몫을 더해야 값이 매초 자연스럽게 움직인다.
        double cycleKwh = (totals.CycleWattHours + _minuteWattHours) / 1000.0;

        var usage = timeOfUse && totals.CycleByPeriod is not null
            ? new CycleUsage(cycleKwh, ToKwh(totals.CycleByPeriod))
            : new CycleUsage(cycleKwh);

        var cycleCost = TariffCalculator.Calculate(tariff, usage);

        double todayKwh = (totals.TodayWattHours + _minuteWattHours) / 1000.0;

        // 오늘 몫은 누진 구간을 다시 계산하지 않고 현재 한계 단가로 곱한다.
        // "오늘 얼마 썼나"에 대한 답으로는 이쪽이 직관에 맞는다.
        double marginal = TariffCalculator.MarginalPricePerKwh(tariff, cycleKwh, now);
        double todayCost = todayKwh * marginal;

        double elapsedFraction = BillingCycle.ElapsedFraction(tariff.BillingCycleStartDay, now);
        double projected = elapsedFraction > 0.01
            ? TariffCalculator.Calculate(tariff, new CycleUsage(cycleKwh / elapsedFraction)).Total
            : cycleCost.Total;

        return new MeteringSnapshot
        {
            Power = reading,
            CycleKwh = cycleKwh,
            CycleCost = cycleCost,
            TodayKwh = todayKwh,
            TodayCost = todayCost,
            ProjectedCycleCost = projected,
            CostPerHour = TariffCalculator.CostPerHour(tariff, cycleKwh, reading.WallWatts, now),
            IsTariffConfigured = Profile.IsTariffConfigured,
            TierWarning = TariffCalculator.GetTierWarning(tariff, cycleKwh),
            GaugeFraction = ComputeGaugeFraction(reading, cycleCost.Total),
            GaugeMode = Profile.GaugeMode,
            Tariff = tariff,
        };
    }

    /// <summary>가정용 PC 한 대가 이 이상을 쓸 수는 없다. 센서가 튄 값을 걸러 내는 선이다.</summary>
    private const double MaxCredibleWatts = 2000;

    /// <summary>
    /// 순간 모드 게이지의 최소 만재 기준.
    ///
    /// 아직 아무것도 관측하지 못했거나 저전력 기기라면, 지금 값이 곧 최고 기록이라
    /// 게이지가 늘 가득 찬 것처럼 보인다. 바닥을 두어 그런 착시를 막는다.
    /// </summary>
    private const double MinimumGaugeFullScaleWatts = 60;

    private double ComputeGaugeFraction(PowerReading reading, double cycleCost)
    {
        double fullScale = Math.Max(MinimumGaugeFullScaleWatts, Profile.ObservedPeakWatts);

        if (Profile.GaugeMode == GaugeMode.Instant)
            return Math.Clamp(reading.WallWatts / fullScale, 0, 1);

        double budget = Profile.Tariff.MonthlyBudget;

        // 예산을 정하지 않았으면 채울 기준이 없다. 순간 전력으로 대신 그린다.
        if (budget <= 0)
            return Math.Clamp(reading.WallWatts / fullScale, 0, 1);

        return Math.Clamp(cycleCost / budget, 0, 1);
    }

    private static Dictionary<string, double> ToKwh(Dictionary<string, double> wattHours)
    {
        var result = new Dictionary<string, double>(wattHours.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in wattHours) result[key] = value / 1000.0;
        return result;
    }

    private static DateTimeOffset FloorToMinute(DateTimeOffset value)
        => new(value.Year, value.Month, value.Day, value.Hour, value.Minute, 0, value.Offset);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();

        lock (_gate)
        {
            FlushMinute();
            _profileStore.Save(Profile);
            _history.Dispose();
            _reader.Dispose();
        }
    }
}
