namespace Bluestaq.Notes.Api.Digest;

/// <summary>Calendar days in the digest time zone, as UTC instants.</summary>
public sealed class DigestSchedule(TimeZoneInfo zone)
{
    public TimeZoneInfo Zone { get; } = zone;

    public DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(utc, Zone);

    /// <summary>The most recent local midnight at or before <paramref name="utc"/>.</summary>
    public DateTime StartOfDay(DateTime utc) => Midnight(ToLocal(utc).Date);

    public DateTime NextMidnight(DateTime utc) => Midnight(ToLocal(utc).Date.AddDays(1));

    /// <summary>The local day that ended at the most recent midnight: [start, end).</summary>
    public (DateTime Start, DateTime End) PreviousDay(DateTime utc)
    {
        var today = ToLocal(utc).Date;
        return (Midnight(today.AddDays(-1)), Midnight(today));
    }

    /// <summary>Start of the local day; in zones whose clocks skip midnight, the first valid time after it.</summary>
    private DateTime Midnight(DateTime localDate)
    {
        var local = DateTime.SpecifyKind(localDate, DateTimeKind.Unspecified);
        while (Zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(15);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }
}
