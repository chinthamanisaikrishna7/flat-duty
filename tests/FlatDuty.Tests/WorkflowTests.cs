using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Xunit;

public sealed class SqlWorkflowFactAttribute : FactAttribute
{
    public SqlWorkflowFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FLATDUTY_TEST_SQL")))
            Skip = "Set FLATDUTY_TEST_SQL to run against a disposable SQL Server.";
    }
}

public class WorkflowTests
{
    [SqlWorkflowFact]
    public async Task Worker_recovers_assignments_rotates_fairly_and_honors_confirmation()
    {
        var server = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FLATDUTY_TEST_SQL"))
        {
            InitialCatalog = "master",
            Pooling = false
        };
        var database = $"FlatDutyTest_{Guid.NewGuid():N}";
        await using var admin = new SqlConnection(server.ConnectionString);
        await admin.OpenAsync();
        using var create = new SqlCommand($"CREATE DATABASE [{database}]", admin);
        await create.ExecuteNonQueryAsync();
        try
        {
            server.InitialCatalog = database;
            await DutyDatabase.InitializeAsync(server.ConnectionString);
            var roster = Enumerable.Range(1, 6).Select(id => new Member($"Member {id}", id)).ToArray();
            var config = new DutyConfig(server.ConnectionString, "test", roster);
            var clock = new CollectionClock();
            var schedule = new CollectionSchedule(new ConfigurationBuilder().Build());
            using var telegram = new FakeTelegram();
            using var http = new HttpClient(telegram);
            DutyWorker StartWorker() => new(config, http, clock, schedule);
            var worker = StartWorker();
            var day = new DateTime(2026, 11, 26);

            // Recover a missed Wednesday assignment at the first Thursday morning tick.
            clock.SetIndiaTime(day.AddHours(6).AddMinutes(30));
            await worker.Tick();
            var owner = Assert.Single(telegram.Recipients);
            worker = StartWorker();
            await worker.Tick();
            Assert.Single(telegram.Recipients);
            clock.SetIndiaTime(day.AddHours(7));
            await worker.Tick();
            var backup = telegram.Recipients.Last();
            Assert.NotEqual(owner, backup);

            telegram.Confirm(roster.First(m => m.ChatId != owner && m.ChatId != backup).ChatId, "2026-11-26");
            await worker.Inbox();
            clock.SetIndiaTime(day.AddHours(7).AddMinutes(5));
            await worker.Tick();
            Assert.Equal(new[] { owner, backup, backup }, telegram.Recipients);
            telegram.Confirm(backup, "2026-11-26");
            await worker.Inbox();
            worker = StartWorker();
            clock.SetIndiaTime(day.AddHours(7).AddMinutes(10));
            await worker.Tick();
            Assert.Equal(3, telegram.Recipients.Count);
            Assert.Contains("Completed. Reminders stopped.", telegram.Acknowledgements);

            var owners = new HashSet<long> { owner };
            for (var turn = 1; turn < 6; turn++)
            {
                day = day.AddDays(day.DayOfWeek == DayOfWeek.Thursday ? 3 : 4);
                clock.SetIndiaTime(day.AddDays(-1).AddHours(21));
                await worker.Tick();
                Assert.True(owners.Add(telegram.Recipients.Last()));
            }
            var beforeCutoff = telegram.Recipients.Count;
            clock.SetIndiaTime(day.AddHours(8));
            await worker.Tick();
            Assert.Equal(beforeCutoff, telegram.Recipients.Count);
        }
        finally
        {
            using var cleanup = new SqlCommand($"DROP DATABASE [{database}]", admin);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    private sealed class CollectionClock : TimeProvider
    {
        private DateTimeOffset utc;
        public override DateTimeOffset GetUtcNow() => utc;
        public void SetIndiaTime(DateTime local) => utc = new DateTimeOffset(local, TimeSpan.FromMinutes(330)).ToUniversalTime();
    }

    private sealed class FakeTelegram : HttpMessageHandler
    {
        public List<long> Recipients { get; } = [];
        public List<string> Acknowledgements { get; } = [];
        private readonly List<object> updates = [];

        public void Confirm(long id, string day)
        {
            var sequence = updates.Count + 1;
            updates.Add(new { update_id = sequence, callback_query = new { id = $"cb{sequence}", from = new { id }, data = $"done:{day}" } });
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            object result = true;
            switch (request.RequestUri!.Segments.Last())
            {
                case "sendMessage":
                    Recipients.Add(body.GetProperty("chat_id").GetInt64());
                    break;
                case "getUpdates":
                    var offset = body.GetProperty("offset").GetInt32();
                    result = updates.Skip(Math.Max(0, offset - 1)).ToArray();
                    break;
                case "answerCallbackQuery":
                    Acknowledgements.Add(body.GetProperty("text").GetString()!);
                    break;
                default:
                    throw new InvalidOperationException("Unexpected Telegram method in test.");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true, result }) };
        }
    }
}
