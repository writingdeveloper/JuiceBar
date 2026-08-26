namespace JuiceBar.Core.Tariff;

/// <summary>청구 주기 경계 계산. 누진 구간 리셋과 "이번 달" 집계의 기준이 된다.</summary>
public static class BillingCycle
{
    /// <summary>
    /// 주어진 시각이 속한 청구 주기의 [시작, 끝) 구간을 돌려준다.
    ///
    /// 시작일이 31일인데 그 달이 30일까지밖에 없는 경우처럼 존재하지 않는 날짜는
    /// 그 달의 마지막 날로 당긴다.
    /// </summary>
    public static (DateTimeOffset Start, DateTimeOffset End) Current(int startDay, DateTimeOffset now)
    {
        startDay = Math.Clamp(startDay, 1, 31);

        var local = now.LocalDateTime;
        int anchorDay = ClampToMonth(startDay, local.Year, local.Month);

        DateTime start = local.Day >= anchorDay
            ? new DateTime(local.Year, local.Month, anchorDay, 0, 0, 0, DateTimeKind.Local)
            : PreviousMonthAnchor(startDay, local);

        DateTime end = NextMonthAnchor(startDay, start);

        return (new DateTimeOffset(start), new DateTimeOffset(end));
    }

    private static DateTime PreviousMonthAnchor(int startDay, DateTime local)
    {
        var previous = local.AddMonths(-1);
        int day = ClampToMonth(startDay, previous.Year, previous.Month);
        return new DateTime(previous.Year, previous.Month, day, 0, 0, 0, DateTimeKind.Local);
    }

    private static DateTime NextMonthAnchor(int startDay, DateTime start)
    {
        var next = start.AddMonths(1);
        int day = ClampToMonth(startDay, next.Year, next.Month);
        return new DateTime(next.Year, next.Month, day, 0, 0, 0, DateTimeKind.Local);
    }

    private static int ClampToMonth(int day, int year, int month)
        => Math.Min(day, DateTime.DaysInMonth(year, month));

    /// <summary>주기가 얼마나 지났는지 (0~1). 월말 예상 요금을 외삽하는 데 쓴다.</summary>
    public static double ElapsedFraction(int startDay, DateTimeOffset now)
    {
        var (start, end) = Current(startDay, now);
        double total = (end - start).TotalSeconds;
        if (total <= 0) return 0;

        return Math.Clamp((now - start).TotalSeconds / total, 0, 1);
    }
}
