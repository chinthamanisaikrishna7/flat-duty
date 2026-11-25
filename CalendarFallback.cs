using System.Globalization;

public static class CalendarFallback
{
    public static string Export(CollectionSchedule schedule, TimeProvider clock)
    {
        var india = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
        var issued = clock.GetUtcNow();
        var local = TimeZoneInfo.ConvertTime(issued, india).DateTime;
        var lines = new List<string> {
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//FlatDuty//Collection fallback//EN",
            "CALSCALE:GREGORIAN"
        };
        for (int offset = 0; offset < 7; offset++)
        {
            var day = local.Date.AddDays(offset);
            if (!schedule.IsCollectionDay(day)) continue;
            var start = day.Add(schedule.Backup.ToTimeSpan());
            if (start < local) start = start.AddDays(7);
            lines.AddRange([
                "BEGIN:VEVENT", $"UID:waste-{day.DayOfWeek}@flatduty.local",
                $"DTSTAMP:{Stamp(issued.UtcDateTime)}",
                $"DTSTART:{Stamp(TimeZoneInfo.ConvertTimeToUtc(start, india))}",
                "DURATION:PT5M", "RRULE:FREQ=WEEKLY", "TRANSP:TRANSPARENT",
                "SUMMARY:Check today's waste collection",
                "DESCRIPTION:Check whether the flat has handed over the waste.",
                "BEGIN:VALARM", "TRIGGER:PT0M", "ACTION:DISPLAY",
                "DESCRIPTION:Check today's waste collection", "END:VALARM", "END:VEVENT"
            ]);
        }
        lines.Add("END:VCALENDAR");
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static string Stamp(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}
