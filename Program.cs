using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using System.Text.Json;
using System.Security.Cryptography;
using System.Globalization;
using Microsoft.Extensions.Configuration.Memory;

var builder = WebApplication.CreateBuilder(args);
// Compose supplies environment variables. For a direct launch, use .env as defaults.
var envPath = Path.Combine(builder.Environment.ContentRootPath, ".env");
if (File.Exists(envPath))
{
    using var stream = File.OpenRead(envPath);
    using var defaults = new ConfigurationManager();
    defaults.AddIniStream(stream);
    var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var entry in defaults.AsEnumerable())
    {
        var value = entry.Value;
        if (value is { Length: >= 2 } && value[0] == '\'' && value[^1] == '\'')
            value = value[1..^1];
        values[entry.Key.Replace("__", ":")] = value;
    }
    builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = values });
}

if (args.Contains("--simulate"))
{
    Environment.ExitCode = CollectionSimulation.Run(args, new CollectionSchedule(builder.Configuration), Console.Out);
    return;
}

var connection = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("Set ConnectionStrings__Default in .env or the process environment.");

if (args.Contains("--check-db"))
{
    try
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        using var probe = new SqlCommand("SELECT 1", db);
        await probe.ExecuteScalarAsync();
        Console.WriteLine("Connected to the configured SQL Server database.");
    }
    catch (SqlException ex)
    {
        Console.Error.WriteLine($"SQL connection failed (SQL error {ex.Number}). Check that the instance is running, the database exists, and the login has access.");
        Environment.ExitCode = 1;
    }
    catch (ArgumentException)
    {
        Console.Error.WriteLine("Invalid SQL connection string. Check the field names and quoting in ConnectionStrings__Default.");
        Environment.ExitCode = 1;
    }
    return;
}

var members = builder.Configuration.GetSection("Members").Get<Member[]>() ?? [];
MemberRegistration.Validate(members);
if (args.SequenceEqual(new[] { "--registrations" }))
{
    await using var db = new SqlConnection(connection);
    await db.OpenAsync();
    using var query = new SqlCommand("SELECT Slot FROM dbo.MemberRegistrations WHERE ChatId IS NOT NULL", db);
    await using var rows = await query.ExecuteReaderAsync();
    var registered = new HashSet<int>();
    while (await rows.ReadAsync()) registered.Add(rows.GetByte(0));
    for (int slot = 0; slot < members.Length; slot++)
        Console.WriteLine($"{members[slot].Name}: {(registered.Contains(slot) ? "Registered" : "Waiting")}");
    Console.WriteLine($"{registered.Count}/{members.Length} registered.");
    return;
}
if (args.Contains("--invites"))
{
    if (args.Length != 2 || args[0] != "--invites" || !MemberRegistration.ValidBotUsername(args[1].TrimStart('@')))
    {
        Console.Error.WriteLine("Usage: --invites YourBotUsernameBot");
        Environment.ExitCode = 2;
        return;
    }
    await DutyDatabase.InitializeAsync(connection);
    await MemberRegistration.InitializeAsync(connection, members);
    var invites = await MemberRegistration.IssueInvitesAsync(connection, members, args[1].TrimStart('@'));
    Console.WriteLine("Share each link privately with its named flatmate. Previous unclaimed links have been replaced.");
    foreach (var invite in invites)
        Console.WriteLine($"{invite.Name}: {invite.Link ?? "Already registered"}");
    return;
}
var token = builder.Configuration["BotToken"];
if (string.IsNullOrWhiteSpace(token) || token == "YOUR_BOT_TOKEN")
    throw new InvalidOperationException("Set BotToken to your Telegram bot token in .env or the process environment.");
builder.Services.AddSingleton(new DutyConfig(connection, token, members));
builder.Services.AddSingleton(new CollectionSchedule(builder.Configuration));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<DutyWorker>().RemoveAllLoggers();
builder.Services.AddHangfire(c => c.UseSqlServerStorage(connection));
builder.Services.AddHangfireServer(o => o.WorkerCount = 2);
var app = builder.Build();
await DutyDatabase.InitializeAsync(connection);
await MemberRegistration.InitializeAsync(connection, members);
var jobs = app.Services.GetRequiredService<IRecurringJobManager>();
jobs.AddOrUpdate<DutyWorker>("duty-tick", w => w.Tick(), "* * * * *");
jobs.AddOrUpdate<DutyWorker>("telegram-inbox", w => w.Inbox(), "* * * * *");
app.MapGet("/", () => Results.Text("FlatDuty is running. Use Telegram to confirm your duty."));
app.Run();

public record Member(string Name, long ChatId);
public record DutyConfig(string Connection, string Token, Member[] Members);
public class DutyWorker(DutyConfig cfg, HttpClient http, TimeProvider clock, CollectionSchedule schedule, ILogger<DutyWorker>? logger = null) {
 private DateTime LocalNow => CollectionSchedule.IndiaTime(clock);
 private async Task<SqlConnection> Open() { var db = new SqlConnection(cfg.Connection); await db.OpenAsync(); return db; }
 private static SqlCommand Cmd(SqlConnection db, string sql, params (string,object)[] args) { var c = new SqlCommand(sql,db); foreach(var (k,v) in args) c.Parameters.AddWithValue(k,v); return c; }
 private readonly TelegramClient telegram = new(http, cfg.Token);
 [DisableConcurrentExecution(120)]
 public async Task Tick() {
  var slot = schedule.At(LocalNow);
  if (slot is null) return;
  var day = slot.Day;
  var key = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
  await using var db=await Open();
  var chats = await MemberRegistration.RegisteredChatsAsync(db);
  if (chats.Length != 6) return;
  await using(var tx=(SqlTransaction)await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable)) {
   async Task<object?> Q(string sql,params (string,object)[] args) { using var c=Cmd(db,sql,args); c.Transaction=tx; return await c.ExecuteScalarAsync(); }
   if(await Q("SELECT OwnerId FROM dbo.Duties WHERE Day=@d",("@d",day)) is null) {
    foreach (var chat in chats)
        await Q("IF NOT EXISTS(SELECT 1 FROM dbo.Rotation WHERE ChatId=@id) INSERT dbo.Rotation VALUES(@id,0)", ("@id", chat));
    var eligible = new List<long>();
    foreach (var chat in chats)
        if (!Convert.ToBoolean(await Q("SELECT Used FROM dbo.Rotation WHERE ChatId=@id", ("@id", chat)))) eligible.Add(chat);
    if (eligible.Count == 0)
    {
        foreach (var chat in chats) await Q("UPDATE dbo.Rotation SET Used=0 WHERE ChatId=@id", ("@id", chat));
        eligible.AddRange(chats);
    }
    var owner = eligible[RandomNumberGenerator.GetInt32(eligible.Count)];
    var backups = chats.Where(chat => chat != owner).ToArray();
    var backup = backups[RandomNumberGenerator.GetInt32(backups.Length)];
    await Q("UPDATE dbo.Rotation SET Used=1 WHERE ChatId=@id",("@id",owner));
    await Q("INSERT dbo.Duties(Day,OwnerId,BackupId) VALUES(@d,@o,@b)",("@d",day),("@o",owner),("@b",backup));
   }
   await tx.CommitAsync();
  }
  long ownerId, backupId;
  using (var readDuty = Cmd(db, "SELECT OwnerId,BackupId,Completed,LastSlot FROM dbo.Duties WHERE Day=@d", ("@d", day)))
  {
      await using var reader = await readDuty.ExecuteReaderAsync();
      await reader.ReadAsync();
      if (reader.GetBoolean(2) || (!reader.IsDBNull(3) && reader.GetString(3) == slot.Key)) return;
      ownerId = reader.GetInt64(0);
      backupId = reader.GetInt64(1);
  }
  var target = slot.Phase == DutyPhase.BackupReminder ? backupId : ownerId;
  var message = slot.Phase switch
  {
      DutyPhase.Assignment => $"You have waste duty tomorrow ({key}). Please prepare wet and dry waste tonight.",
      DutyPhase.BackupReminder => $"Waste collection today ({key}). Backup duty: please help. Confirm only after disposal.",
      _ => $"Waste collection today ({key}). Confirm only after disposal."
  };
  using var checkCompletion = Cmd(db, "SELECT Completed FROM dbo.Duties WHERE Day=@d", ("@d", day));
  if (Convert.ToBoolean(await checkCompletion.ExecuteScalarAsync())) return;
  if (schedule.At(LocalNow) != slot) return;
  await telegram.SendAsync(target, message, key);
  using var recordDelivery = Cmd(db, "UPDATE dbo.Duties SET LastSlot=@s WHERE Day=@d", ("@s", slot.Key), ("@d", day));
  await recordDelivery.ExecuteNonQueryAsync();
 }
 [DisableConcurrentExecution(120)]
 public async Task Inbox() {
  await using var db=await Open(); var offset=Convert.ToInt64(await Cmd(db,"SELECT NextOffset FROM dbo.BotState WHERE Id=1").ExecuteScalarAsync());
  using var json=await telegram.CallAsync("getUpdates",new {offset,timeout=0,allowed_updates=new[]{"callback_query","message"}});
  foreach(var update in json.RootElement.GetProperty("result").EnumerateArray()) {
   if (update.TryGetProperty("message", out var message) && JoinRequest.FromMessage(message) is { } request)
   {
       var response = await MemberRegistration.JoinAsync(db, request, cfg.Members);
       await telegram.ReplyToRegistrationAsync(request.ChatId, response, logger);
   }
   if(update.TryGetProperty("callback_query",out var cb)) {
    var id=cb.GetProperty("from").GetProperty("id").GetInt64(); var data=cb.GetProperty("data").GetString()??""; var reply="Not authorized or invalid duty.";
    if(data.StartsWith("done:")&&DateTime.TryParseExact(data[5..],"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var day)&&day==LocalNow.Date) {
     var changed=await Cmd(db,"UPDATE dbo.Duties SET Completed=1 WHERE Day=@d AND Completed=0 AND (OwnerId=@id OR BackupId=@id)",("@d",day),("@id",id)).ExecuteNonQueryAsync(); reply=changed==1?"Completed. Reminders stopped.":"Already completed or not your duty.";
    }
    using var ack=await telegram.CallAsync("answerCallbackQuery",new {callback_query_id=cb.GetProperty("id").GetString(),text=reply});
   }
   await Cmd(db,"UPDATE dbo.BotState SET NextOffset=@o WHERE Id=1",("@o",update.GetProperty("update_id").GetInt64()+1)).ExecuteNonQueryAsync();
  }
 }
}
