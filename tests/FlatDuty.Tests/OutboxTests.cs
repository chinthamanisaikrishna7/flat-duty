using System.Net;
using System.Net.Http.Json;
using Xunit;

public class OutboxTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(408, true)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public void Only_transient_errors_can_retry(int? code, bool expected) =>
        Assert.Equal(expected, DutyOutbox.Retryable((HttpStatusCode?)code));

    [Fact]
    public async Task Preserves_Telegram_requested_backoff()
    {
        using var client = new HttpClient(new Throttled());
        var telegram = new TelegramClient(client, "test");
        var ex = await Assert.ThrowsAsync<TelegramRateLimitException>(() => telegram.SendAsync(1, "Reminder"));
        Assert.Equal(300, ex.RetryAfterSeconds);
    }

    private sealed class Throttled : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
                Content = JsonContent.Create(new { ok = false, parameters = new { retry_after = 300 } })
            });
    }
}
