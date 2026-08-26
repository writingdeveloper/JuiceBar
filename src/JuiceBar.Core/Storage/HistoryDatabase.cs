using Microsoft.Data.Sqlite;

namespace JuiceBar.Core.Storage;

/// <summary>분 단위로 집계된 사용 이력 한 칸.</summary>
public sealed record MinuteSample(DateTimeOffset MinuteUtc, double WattHours, double AverageWatts);

/// <summary>
/// 사용 이력을 1분 버킷으로 저장한다.
///
/// 초 단위로 남기면 1년에 3천만 행이 되지만 분 단위면 52만 행이라 부담이 없고,
/// 스파크라인·일별·주기별 집계에는 분 해상도로 충분하다.
/// </summary>
public sealed class HistoryDatabase : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public HistoryDatabase(string filePath)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        _connection.Open();
        Initialise();
    }

    private void Initialise()
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;

            CREATE TABLE IF NOT EXISTS minute_samples (
                minute_utc INTEGER PRIMARY KEY,
                watt_hours REAL    NOT NULL,
                avg_watts  REAL    NOT NULL,
                period     TEXT
            );
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// 한 분의 집계를 기록한다. 같은 분이 다시 들어오면 누적한다 —
    /// 앱이 분 중간에 재시작해도 그 분의 앞부분이 사라지지 않게 하기 위해서다.
    /// </summary>
    public void RecordMinute(DateTimeOffset minuteUtc, double wattHours, double averageWatts, string? period)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO minute_samples (minute_utc, watt_hours, avg_watts, period)
            VALUES ($minute, $wh, $watts, $period)
            ON CONFLICT(minute_utc) DO UPDATE SET
                watt_hours = watt_hours + excluded.watt_hours,
                avg_watts  = excluded.avg_watts,
                period     = excluded.period;
            """;

        command.Parameters.AddWithValue("$minute", ToMinuteKey(minuteUtc));
        command.Parameters.AddWithValue("$wh", wattHours);
        command.Parameters.AddWithValue("$watts", averageWatts);
        command.Parameters.AddWithValue("$period", (object?)period ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    public double SumWattHours(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT COALESCE(SUM(watt_hours), 0)
            FROM minute_samples
            WHERE minute_utc >= $from AND minute_utc < $to;
            """;

        command.Parameters.AddWithValue("$from", ToMinuteKey(fromUtc));
        command.Parameters.AddWithValue("$to", ToMinuteKey(toUtc));

        return Convert.ToDouble(command.ExecuteScalar() ?? 0d);
    }

    /// <summary>시간대별 요금제를 위해 구간 이름별로 나눠 집계한다.</summary>
    public Dictionary<string, double> SumWattHoursByPeriod(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var command = _connection.CreateCommand();
        // 구간 이름이 없는 행(단일 단가 요금제 시절에 쌓인 것)은 빈 문자열로 모은다.
        command.CommandText = """
            SELECT COALESCE(period, ''), SUM(watt_hours)
            FROM minute_samples
            WHERE minute_utc >= $from AND minute_utc < $to
            GROUP BY period;
            """;

        command.Parameters.AddWithValue("$from", ToMinuteKey(fromUtc));
        command.Parameters.AddWithValue("$to", ToMinuteKey(toUtc));

        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            result[reader.GetString(0)] = reader.GetDouble(1);

        return result;
    }

    /// <summary>스파크라인용. 최근 <paramref name="minutes"/>분을 오래된 순으로 돌려준다.</summary>
    public IReadOnlyList<MinuteSample> RecentMinutes(int minutes)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT minute_utc, watt_hours, avg_watts
            FROM minute_samples
            ORDER BY minute_utc DESC
            LIMIT $limit;
            """;

        command.Parameters.AddWithValue("$limit", minutes);

        var samples = new List<MinuteSample>(minutes);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            samples.Add(new MinuteSample(
                DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0) * 60),
                reader.GetDouble(1),
                reader.GetDouble(2)));
        }

        samples.Reverse();
        return samples;
    }

    /// <summary>일별 사용량. 이력 화면의 막대 그래프에 쓴다.</summary>
    public IReadOnlyList<(DateTimeOffset Day, double WattHours)> DailyTotals(
        DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            SELECT (minute_utc / 1440) AS day, SUM(watt_hours)
            FROM minute_samples
            WHERE minute_utc >= $from AND minute_utc < $to
            GROUP BY day
            ORDER BY day;
            """;

        command.Parameters.AddWithValue("$from", ToMinuteKey(fromUtc));
        command.Parameters.AddWithValue("$to", ToMinuteKey(toUtc));

        var result = new List<(DateTimeOffset, double)>();

        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add((DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0) * 86400), reader.GetDouble(1)));

        return result;
    }

    /// <summary>오래된 이력을 지운다. 무한정 쌓이지 않게 주기적으로 호출한다.</summary>
    public int Prune(DateTimeOffset olderThanUtc)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM minute_samples WHERE minute_utc < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", ToMinuteKey(olderThanUtc));
        return command.ExecuteNonQuery();
    }

    private static long ToMinuteKey(DateTimeOffset value)
        => value.ToUnixTimeSeconds() / 60;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Dispose();
    }
}
