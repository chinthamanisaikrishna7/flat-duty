using Microsoft.Data.SqlClient;
using System.Net;

public static class DutyOutbox
{
    public const int MaxAttempts = 3;

    // The unique key and the assignment transaction cover both first delivery and a restart.
    public static async Task EnqueueAsync(SqlConnection db, SqlTransaction tx, DutySlot slot)
    {
        using var cmd = new SqlCommand("""
            INSERT dbo.DutyNotifications (Day, Slot, Recipient)
            SELECT d.Day, @slot, CASE WHEN @backup=1 THEN d.BackupId ELSE d.OwnerId END
            FROM dbo.Duties d WHERE d.Day=@day AND d.Completed=0
              AND (d.LastSlot IS NULL OR d.LastSlot<>@slot)
              AND NOT EXISTS (SELECT 1 FROM dbo.DutyNotifications WITH (UPDLOCK,HOLDLOCK)
                              WHERE Day=@day AND Slot=@slot)
            """, db, tx);
        cmd.Parameters.AddWithValue("@day", slot.Day);
        cmd.Parameters.AddWithValue("@slot", slot.Key);
        cmd.Parameters.AddWithValue("@backup", slot.Phase == DutyPhase.BackupReminder);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task DeliverAsync(SqlConnection db, DutySlot slot, CollectionSchedule schedule,
        TimeProvider clock, TelegramClient telegram, ILogger? logger)
    {
        if (schedule.At(CollectionSchedule.IndiaTime(clock)) != slot) return;
        // A lease recovers interrupted attempts. Its duration exceeds the client's HTTP timeout.
        using var claim = new SqlCommand("""
            UPDATE n SET Attempts=Attempts+1, NextAttemptUtc=DATEADD(minute, 2, @now)
            OUTPUT inserted.Recipient, inserted.Attempts
            FROM dbo.DutyNotifications n JOIN dbo.Duties d ON n.Day=d.Day
            WHERE n.Day=@day AND n.Slot=@slot AND d.Completed=0 AND n.State='Pending'
              AND n.Attempts<@max AND (n.NextAttemptUtc IS NULL OR n.NextAttemptUtc<=@now)
              AND NOT EXISTS (SELECT 1 FROM dbo.DutyNotifications WHERE ThrottleUntilUtc>@now)
            """, db);
        claim.Parameters.AddWithValue("@day", slot.Day);
        claim.Parameters.AddWithValue("@slot", slot.Key);
        claim.Parameters.AddWithValue("@max", MaxAttempts);
        claim.Parameters.AddWithValue("@now", clock.GetUtcNow().UtcDateTime);
        long recipient;
        int attempt;
        await using (var rows = await claim.ExecuteReaderAsync())
        {
            if (!await rows.ReadAsync()) return;
            recipient = rows.GetInt64(0);
            attempt = rows.GetInt32(1);
        }
        var dayKey = slot.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var text = slot.Phase switch
        {
            DutyPhase.Assignment => $"You have waste duty tomorrow ({dayKey}). Please prepare wet and dry waste tonight.",
            DutyPhase.BackupReminder => $"Waste collection today ({dayKey}). Backup duty: please help. Confirm only after disposal.",
            _ => $"Waste collection today ({dayKey}). Confirm only after disposal."
        };
        using var check = new SqlCommand("SELECT Completed FROM dbo.Duties WHERE Day=@day", db);
        check.Parameters.AddWithValue("@day", slot.Day);
        if (Convert.ToBoolean(await check.ExecuteScalarAsync()) || schedule.At(CollectionSchedule.IndiaTime(clock)) != slot) return;

        string state = "Sent";
        int? status = null;
        DateTime? throttleUntil = null;
        try
        {
            await telegram.SendAsync(recipient, text, dayKey);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            var code = (ex as HttpRequestException)?.StatusCode;
            status = (int?)code;
            if (ex is TelegramRateLimitException limited)
                throttleUntil = clock.GetUtcNow().UtcDateTime.AddSeconds(limited.RetryAfterSeconds);
            state = Retryable(code) && attempt < MaxAttempts ? "Pending" : "Failed";
            logger?.LogWarning("Duty {Day} slot {Slot} attempt {Attempt} ended {State} (HTTP {Status})",
                dayKey, slot.Key, attempt, state, status);
        }
        using var finish = new SqlCommand("""
            UPDATE dbo.DutyNotifications SET State=@state, LastHttpStatus=@status, ThrottleUntilUtc=@throttle,
                NextAttemptUtc=CASE WHEN @state='Pending' THEN DATEADD(minute,1,@now) END
            WHERE Day=@day AND Slot=@slot AND Attempts=@attempt AND State='Pending'
            """, db);
        finish.Parameters.AddWithValue("@day", slot.Day);
        finish.Parameters.AddWithValue("@slot", slot.Key);
        finish.Parameters.AddWithValue("@attempt", attempt);
        finish.Parameters.AddWithValue("@state", state);
        finish.Parameters.Add("@status", System.Data.SqlDbType.Int).Value = (object?)status ?? DBNull.Value;
        finish.Parameters.Add("@throttle", System.Data.SqlDbType.DateTime2).Value = (object?)throttleUntil ?? DBNull.Value;
        finish.Parameters.AddWithValue("@now", clock.GetUtcNow().UtcDateTime);
        await finish.ExecuteNonQueryAsync();
    }

    public static bool Retryable(HttpStatusCode? status) =>
        status is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests || (int)status >= 500;
}
