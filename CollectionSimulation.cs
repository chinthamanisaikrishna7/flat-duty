using System.Globalization;

public static class CollectionSimulation
{
    public static int Run(string[] args, CollectionSchedule schedule, TextWriter output)
    {
        if (args.Length is < 2 or > 3 || args[0] != "--simulate" ||
            !DateTime.TryParseExact(args[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            output.WriteLine("Usage: --simulate yyyy-MM-dd [HH:mm confirmation time IST]");
            return 2;
        }
        if (day.Date == DateTime.MinValue.Date || day.Date == DateTime.MaxValue.Date)
        {
            output.WriteLine("Choose a date with an adjacent calendar day.");
            return 2;
        }
        if (!schedule.IsCollectionDay(day))
        {
            output.WriteLine($"{day:yyyy-MM-dd} is not a collection day in the configured schedule.");
            return 2;
        }
        TimeOnly? confirmation = null;
        if (args.Length == 3)
        {
            if (!TimeOnly.TryParseExact(args[2], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                output.WriteLine("Use HH:mm for the collection-day confirmation time.");
                return 2;
            }
            confirmation = time;
        }
        output.WriteLine($"Collection {day:yyyy-MM-dd} (IST dry run)");
        string? lastSlot = null;
        for (var now = day.AddDays(-1).Add(schedule.Assignment.ToTimeSpan()); now <= day.Add(schedule.Cutoff.ToTimeSpan()); now = now.AddMinutes(1))
        {
            var time = TimeOnly.FromDateTime(now);
            if (now.Date == day && time == confirmation)
            {
                output.WriteLine($"{now:yyyy-MM-dd HH:mm}  Disposal confirmed. Reminders stopped.");
                return 0;
            }
            var slot = schedule.At(now);
            if (slot is null || slot.Day != day || slot.Key == lastSlot) continue;
            output.WriteLine($"{now:yyyy-MM-dd HH:mm}  {slot.Phase}");
            lastSlot = slot.Key;
        }
        output.WriteLine($"{day:yyyy-MM-dd} {schedule.Cutoff:HH:mm}  Cutoff reached without confirmation.");
        if (confirmation > schedule.Cutoff)
            output.WriteLine($"{day:yyyy-MM-dd} {confirmation:HH:mm}  Late disposal confirmed.");
        return 0;
    }
}
