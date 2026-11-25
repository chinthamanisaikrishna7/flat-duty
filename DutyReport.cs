using Microsoft.Data.SqlClient;

public static class DutyReport
{
    public static async Task WriteAsync(string connection, CollectionSchedule schedule, DateTime now, TextWriter output)
    {
        var firstDay = now.Date.AddDays(-6);
        var duties = new Dictionary<DateTime, (bool Completed, int Sent, int Failed, int Pending)>();
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        using var query = new SqlCommand("""
            SELECT d.Day,d.Completed,
                SUM(CASE WHEN n.State='Sent' THEN 1 ELSE 0 END),
                SUM(CASE WHEN n.State='Failed' THEN 1 ELSE 0 END),
                SUM(CASE WHEN n.State='Pending' THEN 1 ELSE 0 END)
            FROM dbo.Duties d LEFT JOIN dbo.DutyNotifications n ON n.Day=d.Day
            WHERE d.Day BETWEEN @first AND @last GROUP BY d.Day,d.Completed
            """, db);
        query.Parameters.AddWithValue("@first", firstDay);
        query.Parameters.AddWithValue("@last", now.Date.AddDays(1));
        await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            duties.Add(reader.GetDateTime(0), (reader.GetBoolean(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4)));
        output.WriteLine("Collection (IST) | Disposal | Telegram slots: sent / failed / unacknowledged");
        for (var day = firstDay; day <= now.Date.AddDays(1); day = day.AddDays(1))
        {
            var assigned = duties.TryGetValue(day, out var duty);
            if (!assigned && (!schedule.IsCollectionDay(day) || day > now.Date)) continue;
            string state = !assigned ? "No assignment recorded" : duty.Completed ? "Confirmed" :
                now >= day.Add(schedule.Cutoff.ToTimeSpan()) ? "Unconfirmed after cutoff" : "Open";
            output.WriteLine($"{day:yyyy-MM-dd} | {state} | {duty.Sent} / {duty.Failed} / {duty.Pending}");
        }
    }
}
