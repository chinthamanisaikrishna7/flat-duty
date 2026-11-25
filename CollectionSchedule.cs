using System.Globalization;

public enum DutyPhase { Assignment, OwnerReminder, BackupReminder }

public sealed record DutySlot(DateTime Day, DutyPhase Phase, string Key);

public sealed class CollectionSchedule
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata");
    private readonly HashSet<DayOfWeek> days;
    public TimeOnly Assignment { get; }
    public TimeOnly Start { get; }
    public TimeOnly Backup { get; }
    public TimeOnly Cutoff { get; }
    public int IntervalMinutes { get; }

    public CollectionSchedule(IConfiguration config)
    {
        days = [];
        foreach (var name in (config["Schedule:CollectionDays"] ?? "Thursday,Sunday").Split(',', StringSplitOptions.TrimEntries))
        {
            if (!Enum.GetNames<DayOfWeek>().Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Schedule__CollectionDays must contain comma-separated weekday names.");
            days.Add(Enum.Parse<DayOfWeek>(name, true));
        }
        Assignment = ReadTime(config, "Schedule:AssignmentTime", "21:00");
        Start = ReadTime(config, "Schedule:ReminderStart", "06:30");
        Backup = ReadTime(config, "Schedule:BackupTime", "07:00");
        Cutoff = ReadTime(config, "Cutoff", "08:00");
        IntervalMinutes = config.GetValue("Schedule:IntervalMinutes", 5);
        if (IntervalMinutes < 1 || !(Start < Backup && Backup < Cutoff))
            throw new InvalidOperationException("Use a positive reminder interval and times with ReminderStart < BackupTime < Cutoff.");
        if (Cutoff > Assignment && days.Any(day => days.Contains((DayOfWeek)(((int)day + 1) % 7))))
            throw new InvalidOperationException("On consecutive collection days the cutoff must be at or before the next assignment.");
    }

    private static TimeOnly ReadTime(IConfiguration config, string key, string fallback) =>
        TimeOnly.TryParseExact(config[key] ?? fallback, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value)
            ? value : throw new InvalidOperationException($"{key} must use HH:mm.");

    public static DateTime IndiaTime(TimeProvider clock) => TimeZoneInfo.ConvertTime(clock.GetUtcNow(), India).DateTime;
    public bool IsCollectionDay(DateTime day) => days.Contains(day.DayOfWeek);

    public DutySlot? At(DateTime local)
    {
        var time = TimeOnly.FromDateTime(local);
        var tomorrow = local.Date.AddDays(1);
        if (time >= Assignment && IsCollectionDay(tomorrow))
            return new(tomorrow, DutyPhase.Assignment, "evening");
        if (!IsCollectionDay(local) || time < Start || time >= Cutoff)
            return null;

        var phase = time < Backup ? DutyPhase.OwnerReminder : DutyPhase.BackupReminder;
        var index = (int)(time - Start).TotalMinutes / IntervalMinutes;
        // Keep existing slot keys on aligned schedules. An off-grid escalation starts a new slot immediately.
        var key = $"morning-{index}";
        if (phase == DutyPhase.BackupReminder && (int)(Backup - Start).TotalMinutes % IntervalMinutes != 0)
        {
            var backupIndex = (int)(time - Backup).TotalMinutes / IntervalMinutes;
            key = $"backup-{backupIndex}";
        }
        return new(local.Date, phase, key);
    }
}
