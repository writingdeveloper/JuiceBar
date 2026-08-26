using JuiceBar.Core.Storage;

namespace JuiceBar.Tests;

/// <summary>
/// 이력 DB 중에서도 "지우는" 쪽.
///
/// 누적 초기화는 되돌릴 수 없으므로, 지워야 할 것만 지우는지 실제 파일로 확인한다.
/// </summary>
public sealed class HistoryDatabaseTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("juicebar-history").FullName;
    private readonly HistoryDatabase _history;

    private static readonly DateTimeOffset Noon =
        new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    public HistoryDatabaseTests()
        => _history = new HistoryDatabase(Path.Combine(_directory, "history.sqlite"));

    private void Record(DateTimeOffset minute, double wattHours)
        => _history.RecordMinute(minute, wattHours, wattHours * 60, null);

    [Fact]
    public void Deleting_a_range_removes_only_what_is_inside_it()
    {
        Record(Noon.AddMinutes(-10), 1.0);   // 구간 앞
        Record(Noon.AddMinutes(1), 2.0);     // 구간 안
        Record(Noon.AddMinutes(2), 3.0);     // 구간 안
        Record(Noon.AddMinutes(30), 4.0);    // 구간 뒤

        _history.DeleteRange(Noon, Noon.AddMinutes(10));

        // 지운 구간만 사라지고 앞뒤는 그대로여야 한다 —
        // 이번 주기를 지운다고 지난 주기까지 날아가면 안 된다.
        Assert.Equal(1.0, _history.SumWattHours(Noon.AddHours(-1), Noon), precision: 6);
        Assert.Equal(0.0, _history.SumWattHours(Noon, Noon.AddMinutes(10)), precision: 6);
        Assert.Equal(4.0, _history.SumWattHours(Noon.AddMinutes(10), Noon.AddHours(1)), precision: 6);
    }

    [Fact]
    public void Deleting_reports_how_much_was_cleared()
    {
        Record(Noon.AddMinutes(1), 2.0);
        Record(Noon.AddMinutes(2), 3.0);

        Assert.Equal(5.0, _history.DeleteRange(Noon, Noon.AddMinutes(10)), precision: 6);
    }

    [Fact]
    public void Deleting_an_empty_range_reports_nothing_and_does_not_throw()
    {
        Record(Noon.AddMinutes(1), 2.0);

        Assert.Equal(0.0, _history.DeleteRange(Noon.AddHours(5), Noon.AddHours(6)), precision: 6);
        Assert.Equal(2.0, _history.SumWattHours(Noon, Noon.AddMinutes(10)), precision: 6);
    }

    [Fact]
    public void Recording_into_the_same_minute_accumulates()
    {
        // 앱이 분 중간에 재시작해도 그 분의 앞부분이 사라지면 안 된다.
        Record(Noon.AddMinutes(1), 2.0);
        Record(Noon.AddMinutes(1), 1.5);

        Assert.Equal(3.5, _history.SumWattHours(Noon, Noon.AddMinutes(10)), precision: 6);
    }

    [Fact]
    public void Recording_after_a_reset_starts_from_zero_again()
    {
        Record(Noon.AddMinutes(1), 2.0);
        _history.DeleteRange(Noon, Noon.AddMinutes(10));
        Record(Noon.AddMinutes(3), 0.5);

        Assert.Equal(0.5, _history.SumWattHours(Noon, Noon.AddMinutes(10)), precision: 6);
    }

    public void Dispose()
    {
        _history.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
