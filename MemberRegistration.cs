using Microsoft.Data.SqlClient;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

public sealed record JoinRequest(long ChatId, string? Code)
{
    public static JoinRequest? FromMessage(JsonElement message)
    {
        if (!message.TryGetProperty("text", out var text) ||
            !message.TryGetProperty("chat", out var chat) || chat.GetProperty("type").GetString() != "private" ||
            !message.TryGetProperty("from", out var sender) || sender.GetProperty("is_bot").GetBoolean())
            return null;
        var id = chat.GetProperty("id").GetInt64();
        if (id <= 0 || sender.GetProperty("id").GetInt64() != id) return null;
        var words = text.GetString()?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words is not { Length: > 0 } || words[0] != "/start") return null;
        return new(id, words.Length == 2 ? words[1] : null);
    }
}

public static class MemberRegistration
{
    public static void Validate(Member[] roster)
    {
        var configured = roster.Where(m => m.ChatId > 0).Select(m => m.ChatId);
        if (roster.Length != 6 || roster.Any(m => string.IsNullOrWhiteSpace(m.Name) || m.ChatId < 0) ||
            configured.Distinct().Count() != configured.Count())
            throw new InvalidOperationException("Configure six names with ChatId=0 for invites, or distinct positive chat IDs for existing members.");
    }

    public static async Task InitializeAsync(string connection, Member[] roster)
    {
        Validate(roster);
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync(IsolationLevel.Serializable);
        for (int slot = 0; slot < roster.Length; slot++)
        {
            using var seed = new SqlCommand("""
                IF NOT EXISTS (SELECT 1 FROM dbo.MemberRegistrations WITH (UPDLOCK, HOLDLOCK) WHERE Slot=@slot)
                    INSERT dbo.MemberRegistrations(Slot) VALUES(@slot);
                IF @chat > 0
                BEGIN
                    IF EXISTS (SELECT 1 FROM dbo.MemberRegistrations WHERE Slot=@slot AND ChatId<>@chat)
                        THROW 50001, 'Configured chat ID conflicts with the registered flatmate.', 1;
                    UPDATE dbo.MemberRegistrations SET ChatId=@chat, InviteHash=NULL WHERE Slot=@slot AND ChatId IS NULL;
                END
                """, db, tx);
            seed.Parameters.AddWithValue("@slot", slot);
            seed.Parameters.AddWithValue("@chat", roster[slot].ChatId);
            await seed.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    public static bool ValidBotUsername(string username) =>
        Regex.IsMatch(username, @"\A[A-Za-z][A-Za-z0-9_]{1,28}bot\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static async Task<List<(string Name, string? Link)>> IssueInvitesAsync(string connection, Member[] roster, string username)
    {
        if (!ValidBotUsername(username)) throw new ArgumentException("Enter the bot username from BotFather.");
        var invites = new List<(string, string?)>();
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        await using var tx = (SqlTransaction)await db.BeginTransactionAsync();
        for (int slot = 0; slot < roster.Length; slot++)
        {
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            using var update = new SqlCommand("UPDATE dbo.MemberRegistrations SET InviteHash=@hash WHERE Slot=@slot AND ChatId IS NULL", db, tx);
            update.Parameters.AddWithValue("@slot", slot);
            update.Parameters.AddWithValue("@hash", Hash(code));
            var updated = await update.ExecuteNonQueryAsync();
            invites.Add((roster[slot].Name, updated == 1 ? $"https://t.me/{username}?start={code}" : null));
        }
        await tx.CommitAsync();
        return invites;
    }

    private static byte[] Hash(string code) => SHA256.HashData(Encoding.ASCII.GetBytes(code));

    public static async Task<string> JoinAsync(SqlConnection db, JoinRequest request, Member[] roster)
    {
        if (request.Code is { Length: 32 } code && code.All(char.IsAsciiHexDigit))
        {
            // A claim consumes the invite. Replaying an update returns the same membership.
            using var claim = new SqlCommand("""
                UPDATE dbo.MemberRegistrations WITH (TABLOCKX)
                SET ChatId=@chat, InviteHash=NULL
                WHERE InviteHash=@hash AND ChatId IS NULL
                    AND NOT EXISTS (SELECT 1 FROM dbo.MemberRegistrations WHERE ChatId=@chat);
                """, db);
            claim.Parameters.AddWithValue("@chat", request.ChatId);
            claim.Parameters.AddWithValue("@hash", Hash(code));
            await claim.ExecuteNonQueryAsync();
        }
        using var status = new SqlCommand("""
            SELECT Slot, (SELECT COUNT(ChatId) FROM dbo.MemberRegistrations)
            FROM dbo.MemberRegistrations WHERE ChatId=@chat
            """, db);
        status.Parameters.AddWithValue("@chat", request.ChatId);
        await using var result = await status.ExecuteReaderAsync();
        if (!await result.ReadAsync()) return "Open your personal invite link from the flat organizer and press Start. If the link has expired, ask for a new one.";
        return $"You're registered as {roster[result.GetByte(0)].Name}. {result.GetInt32(1)}/6 flatmates ready.";
    }

    public static async Task<long[]> RegisteredChatsAsync(SqlConnection db)
    {
        using var query = new SqlCommand("SELECT ChatId FROM dbo.MemberRegistrations WHERE ChatId IS NOT NULL ORDER BY Slot", db);
        await using var reader = await query.ExecuteReaderAsync();
        var chats = new List<long>(6);
        while (await reader.ReadAsync()) chats.Add(reader.GetInt64(0));
        return chats.ToArray();
    }
}
