using Microsoft.Data.SqlClient;

public static class DutyDatabase
{
    public static async Task InitializeAsync(string connection)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        using var command = new SqlCommand("""
        IF OBJECT_ID('dbo.Duties') IS NULL CREATE TABLE dbo.Duties (Day date PRIMARY KEY, OwnerId bigint NOT NULL, BackupId bigint NOT NULL, Completed bit NOT NULL DEFAULT 0, LastSlot varchar(32) NULL);
        IF OBJECT_ID('dbo.Rotation') IS NULL CREATE TABLE dbo.Rotation (ChatId bigint PRIMARY KEY, Used bit NOT NULL DEFAULT 0);
        IF OBJECT_ID('dbo.BotState') IS NULL CREATE TABLE dbo.BotState (Id int PRIMARY KEY, NextOffset bigint NOT NULL);
        IF NOT EXISTS (SELECT 1 FROM dbo.BotState WHERE Id=1) INSERT dbo.BotState VALUES (1,0);
        IF OBJECT_ID('dbo.MemberRegistrations') IS NULL
            CREATE TABLE dbo.MemberRegistrations (
                Slot tinyint NOT NULL PRIMARY KEY CHECK (Slot BETWEEN 0 AND 5),
                ChatId bigint NULL CHECK (ChatId > 0),
                InviteHash binary(32) NULL
            );
        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('dbo.MemberRegistrations') AND name = 'UX_MemberRegistrations_Chat')
            CREATE UNIQUE INDEX UX_MemberRegistrations_Chat ON dbo.MemberRegistrations(ChatId) WHERE ChatId IS NOT NULL;
        """, db);
        await command.ExecuteNonQueryAsync();
    }
}
