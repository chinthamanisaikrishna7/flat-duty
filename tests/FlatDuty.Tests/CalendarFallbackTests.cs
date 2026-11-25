using Microsoft.Extensions.Configuration;
using Xunit;

public class CalendarFallbackTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void Default_backup_creates_two_weekly_series_with_ist_times_and_a_display_alarm()
    {
        var schedule = new CollectionSchedule(new ConfigurationBuilder().Build());
        var calendar = CalendarFallback.Export(schedule, new Clock(new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero)));
        Assert.Contains("DTSTART:20261126T013000Z\r\n", calendar);
        Assert.Contains("DTSTART:20261129T013000Z\r\n", calendar);
        Assert.Equal(2, calendar.Split("RRULE:FREQ=WEEKLY").Length - 1);
        Assert.Equal(2, calendar.Split("TRIGGER:PT0M").Length - 1);
        Assert.EndsWith("END:VCALENDAR\r\n", calendar);
    }

    [Fact]
    public void Passed_local_midnight_event_starts_next_week_on_correct_utc_day()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["Schedule:CollectionDays"]="Thursday", ["Schedule:ReminderStart"]="00:01", ["Schedule:BackupTime"]="00:10"
        }).Build();
        var calendar = CalendarFallback.Export(new CollectionSchedule(config),
            new Clock(new DateTimeOffset(2026, 11, 25, 19, 0, 0, TimeSpan.Zero)));
        Assert.Contains("DTSTART:20261202T184000Z", calendar);
        Assert.DoesNotContain("waste-Sunday", calendar);
    }
}
