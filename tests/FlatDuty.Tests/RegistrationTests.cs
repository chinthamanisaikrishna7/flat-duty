using System.Text.Json;
using Xunit;

public class RegistrationTests
{
    [Theory]
    [InlineData("private", 11, 11, false, "/start", true, null)]
    [InlineData("private", 11, 11, false, "/start AB12", true, "AB12")]
    [InlineData("private", 11, 11, false, "/start extra words", true, null)]
    [InlineData("private", 11, 11, false, "/help", false, null)]
    [InlineData("group", -1, 11, false, "/start AB12", false, null)]
    [InlineData("private", 11, 12, false, "/start AB12", false, null)]
    [InlineData("private", 11, 11, true, "/start AB12", false, null)]
    public void Accepts_start_only_from_sender_in_private_chat(string type, long chat, long sender, bool bot, string text, bool accepted, string? code)
    {
        var message = JsonSerializer.SerializeToElement(new { chat = new { id = chat, type }, from = new { id = sender, is_bot = bot }, text });
        Assert.Equal(accepted ? new JoinRequest(chat, code) : null, JoinRequest.FromMessage(message));
    }

    [Fact]
    public void Supports_new_mixed_and_manually_configured_rosters()
    {
        var roster = Enumerable.Range(1, 6).Select(i => new Member($"Flatmate {i}", 0)).ToArray();
        MemberRegistration.Validate(roster);
        for (var i = 0; i < roster.Length; i++)
        {
            roster[i] = roster[i] with { ChatId = i + 1 };
            MemberRegistration.Validate(roster);
        }
        roster[1] = roster[0];
        Assert.Throws<InvalidOperationException>(() => MemberRegistration.Validate(roster));
    }
}
