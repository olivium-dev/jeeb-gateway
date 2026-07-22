namespace JeebGateway.Controllers.V1;

/// <summary>Pure UTC date-range rules shared by the orders-list actions.</summary>
internal static class OrderDateRangeFilter
{
    public static bool IsValid(DateTimeOffset? fromDate, DateTimeOffset? toDate)
    {
        if (fromDate is null || toDate is null)
            return true;

        return toDate.Value.ToUniversalTime() > fromDate.Value.ToUniversalTime();
    }

    public static bool Includes(
        DateTimeOffset createdAt,
        DateTimeOffset? fromDate,
        DateTimeOffset? toDate)
    {
        var createdAtUtc = createdAt.ToUniversalTime();
        var fromDateUtc = fromDate?.ToUniversalTime();
        var toDateUtc = toDate?.ToUniversalTime();

        return (fromDateUtc is null || createdAtUtc >= fromDateUtc.Value)
            && (toDateUtc is null || createdAtUtc < toDateUtc.Value);
    }
}
