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

    // ── 스파크라인이 보는 창 ───────────────────────────────
    //
    // "최근 60분"이라는 이름표를 달고 며칠 전 데이터를 그리면 안 된다.
    // 예전에는 시간과 무관하게 마지막 N 행을 가져와서 실제로 그런 일이 있었다.

    [Fact]
    public void The_recent_window_is_measured_in_time_not_in_rows()
    {
        Record(Noon.AddMinutes(-5), 1.0);
        Record(Noon.AddMinutes(-2), 2.0);

        // 하루 전 기록. 행 수로 세면 이것도 "최근"에 들어와 버린다.
        Record(Noon.AddDays(-1), 9.0);

        var recent = _history.RecentMinutes(60, Noon);

        Assert.Equal(2, recent.Count);
        Assert.All(recent, s => Assert.True(s.MinuteUtc >= Noon.AddMinutes(-60)));
    }

    [Fact]
    public void An_idle_machine_shows_nothing_rather_than_something_stale()
    {
        // PC 를 하루 꺼 뒀다면 최근 한 시간에는 아무 일도 없었던 게 맞다.
        Record(Noon.AddDays(-1), 5.0);

        Assert.Empty(_history.RecentMinutes(60, Noon));
    }

    [Fact]
    public void The_window_is_returned_oldest_first_so_the_line_runs_left_to_right()
    {
        Record(Noon.AddMinutes(-3), 1.0);
        Record(Noon.AddMinutes(-1), 2.0);
        Record(Noon.AddMinutes(-2), 3.0);

        var recent = _history.RecentMinutes(60, Noon);

        Assert.Equal(
            [Noon.AddMinutes(-3), Noon.AddMinutes(-2), Noon.AddMinutes(-1)],
            recent.Select(s => s.MinuteUtc));
    }

    [Fact]
    public void The_window_never_returns_more_points_than_asked_for()
    {
        for (int i = 1; i <= 90; i++) Record(Noon.AddMinutes(-i), 0.1);

        Assert.Equal(60, _history.RecentMinutes(60, Noon).Count);
    }

    public void Dispose()
    {
        _history.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
