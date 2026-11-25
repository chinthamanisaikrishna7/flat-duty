using System.Globalization;
using Microsoft.Extensions.Configuration;
using Xunit;

public class ScheduleTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static CollectionSchedule Schedule(params (string Key, string Value)[] settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(p => p.Key, p => (string?)p.Value)).Build());

    [Theory]
    [InlineData("2026-11-25 20:59", null)]
    [InlineData("2026-11-25 21:00", "evening")]
    [InlineData("2026-11-26 06:29", null)]
    [InlineData("2026-11-26 06:30", "morning-0")]
    [InlineData("2026-11-26 06:34", "morning-0")]
    [InlineData("2026-11-26 06:35", "morning-1")]
    [InlineData("2026-11-26 07:59", "morning-17")]
    [InlineData("2026-11-26 08:00", null)]
    [InlineData("2026-11-27 06:30", null)]
    [InlineData("2026-11-28 23:59", "evening")]
    [InlineData("2026-11-29 06:30", "morning-0")]
    public void Uses_expected_slot(string local, string? key) =>
        Assert.Equal(key, Schedule().At(DateTime.Parse(local, CultureInfo.InvariantCulture))?.Key);

    [Fact]
    public async Task Worker_is_idle_after_cutoff_even_if_host_uses_utc()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 26, 2, 30, 0, TimeSpan.Zero));
        Assert.Equal(new DateTime(2026, 11, 26, 8, 0, 0), CollectionSchedule.IndiaTime(clock));
        using var http = new HttpClient();
        var worker = new DutyWorker(new DutyConfig("invalid", "unused", []), http, clock, Schedule());
        await worker.Tick();
    }

    [Fact]
    public void Custom_schedule_switches_to_backup_even_between_owner_slots()
    {
        var schedule = Schedule(("Schedule:CollectionDays","Monday"), ("Schedule:ReminderStart","09:00"),
            ("Schedule:BackupTime","09:07"), ("Cutoff","09:13"), ("Schedule:AssignmentTime","19:15"));
        var day = new DateTime(2026,11,30);
        Assert.Equal(day, schedule.At(day.AddDays(-1).AddHours(19).AddMinutes(15))?.Day);
        Assert.Equal(DutyPhase.OwnerReminder, schedule.At(day.AddHours(9).AddMinutes(6))?.Phase);
        var backup = schedule.At(day.AddHours(9).AddMinutes(7));
        Assert.Equal(DutyPhase.BackupReminder, backup?.Phase);
        Assert.Equal(backup, schedule.At(day.AddHours(9).AddMinutes(11)));
        Assert.NotEqual(backup, schedule.At(day.AddHours(9).AddMinutes(12)));
        Assert.Null(schedule.At(day.AddHours(9).AddMinutes(13)));
    }

    [Theory]
    [InlineData("Schedule:CollectionDays","8")]
    [InlineData("Schedule:ReminderStart","25:00")]
    [InlineData("Schedule:BackupTime","06:29")]
    [InlineData("Cutoff","07:00")]
    [InlineData("Schedule:IntervalMinutes","0")]
    public void Rejects_invalid_schedule(string key, string value) =>
        Assert.Throws<InvalidOperationException>(() => Schedule((key, value)));

    [Fact]
    public void Simulates_all_slots_or_stops_at_confirmation()
    {
        using var missed = new StringWriter();
        Assert.Equal(0, CollectionSimulation.Run(["--simulate","2026-11-26"], Schedule(), missed));
        Assert.Equal(6, missed.ToString().Split("OwnerReminder").Length - 1);
        Assert.Equal(12, missed.ToString().Split("BackupReminder").Length - 1);
        Assert.Contains("08:00  Cutoff", missed.ToString());
        using var confirmed = new StringWriter();
        Assert.Equal(0, CollectionSimulation.Run(["--simulate","2026-11-26","07:00"], Schedule(), confirmed));
        Assert.Contains("07:00  Disposal confirmed", confirmed.ToString());
        Assert.DoesNotContain("BackupReminder", confirmed.ToString());
    }
}
