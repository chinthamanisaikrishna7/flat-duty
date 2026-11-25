using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

public class TelegramReplyTests
{
    [Fact]
    public async Task Plain_registration_reply_omits_markup_and_duty_keeps_button()
    {
        using var handler = new Handler(HttpStatusCode.OK);
        using var http = new HttpClient(handler);
        var client = new TelegramClient(http, "test-token");
        await client.ReplyToRegistrationAsync(123, "Joined", null);
        Assert.False(handler.Body.TryGetProperty("reply_markup", out _));
        await client.SendAsync(123, "Dispose", "2026-11-26");
        Assert.Equal("done:2026-11-26", handler.Body.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString());
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(403, false)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public async Task Only_permanent_registration_reply_failures_are_skipped(int status, bool retry)
    {
        using var handler = new Handler((HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var client = new TelegramClient(http, "test-token");
        var error = await Record.ExceptionAsync(() => client.ReplyToRegistrationAsync(123,"Joined",null));
        if (!retry) { Assert.Null(error); return; }
        Assert.Equal((HttpStatusCode)status, Assert.IsType<HttpRequestException>(error).StatusCode);
        Assert.Contains("test description", error!.Message);
    }

    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        public JsonElement Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            return new HttpResponseMessage(status) { Content = JsonContent.Create(new { ok = status == HttpStatusCode.OK, description = "test description" }) };
        }
    }
}
