using System.Net;
using System.Text.Json;

public sealed class TelegramRateLimitException(int seconds) : HttpRequestException(
    $"Telegram requested a retry after {seconds} seconds.", null, HttpStatusCode.TooManyRequests)
{
    public int RetryAfterSeconds { get; } = seconds;
}

public sealed class TelegramClient(HttpClient http, string botToken)
{
    public async Task<JsonDocument> CallAsync(string method, object body)
    {
        using var response = await http.PostAsJsonAsync($"https://api.telegram.org/bot{botToken}/{method}", body);
        var content = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var reason = "No Telegram description.";
            try
            {
                using var error = JsonDocument.Parse(content);
                if (response.StatusCode == HttpStatusCode.TooManyRequests &&
                    error.RootElement.ValueKind == JsonValueKind.Object &&
                    error.RootElement.TryGetProperty("parameters", out var parameters) &&
                    parameters.ValueKind == JsonValueKind.Object &&
                    parameters.TryGetProperty("retry_after", out var delay) && delay.ValueKind == JsonValueKind.Number &&
                    delay.TryGetInt32(out var seconds) && seconds > 0)
                    throw new TelegramRateLimitException(seconds);
                if (error.RootElement.ValueKind == JsonValueKind.Object &&
                    error.RootElement.TryGetProperty("description", out var value) && value.ValueKind == JsonValueKind.String)
                    reason = value.GetString()!.Replace(botToken, "[redacted]").ReplaceLineEndings(" ");
            }
            catch (JsonException) { /* Non-JSON proxy errors still carry a useful HTTP status. */ }
            throw new HttpRequestException($"Telegram {method} failed ({(int)response.StatusCode}). {reason}", null, response.StatusCode);
        }
        var json = JsonDocument.Parse(content);
        if (json.RootElement.GetProperty("ok").GetBoolean()) return json;
        json.Dispose();
        throw new InvalidOperationException($"Telegram rejected {method}.");
    }

    public async Task SendAsync(long chatId, string text, string? day = null)
    {
        object body = day is null ? new { chat_id = chatId, text } : new {
            chat_id = chatId, text,
            reply_markup = new { inline_keyboard = new[] { new[] { new { text = "Waste disposed ✓", callback_data = "done:" + day } } } }
        };
        using var result = await CallAsync("sendMessage", body);
    }

    public async Task ReplyToRegistrationAsync(long chatId, string text, ILogger? logger)
    {
        try { await SendAsync(chatId, text); }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden)
        {
            logger?.LogWarning("Telegram rejected the registration reply. Continuing to the next update. {Reason}", ex.Message);
        }
    }
}
