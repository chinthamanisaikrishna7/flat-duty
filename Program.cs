using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
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

var token = builder.Configuration["BotToken"];
if (string.IsNullOrWhiteSpace(token) || token == "YOUR_BOT_TOKEN")
    throw new InvalidOperationException("Set BotToken to your Telegram bot token in .env or the process environment.");
var members = builder.Configuration.GetSection("Members").Get<Member[]>() ?? [];
if (members.Length != 6 || members.Select(m => m.ChatId).Distinct().Count() != 6 || members.Any(m => m.ChatId <= 0 || string.IsNullOrWhiteSpace(m.Name))) throw new Exception("Configure six distinct private Telegram chat IDs and names.");
var cutoff = TimeOnly.Parse(builder.Configuration["Cutoff"] ?? "08:00");
if (cutoff <= new TimeOnly(7,0)) throw new Exception("Cutoff must be after 07:00.");
builder.Services.AddSingleton(new DutyConfig(connection, token, members, cutoff));
builder.Services.AddHttpClient<DutyWorker>();
builder.Services.AddHangfire(c => c.UseSqlServerStorage(connection));
builder.Services.AddHangfireServer(o => o.WorkerCount = 2);
var app = builder.Build();
await using (var db = new SqlConnection(connection)) {
 await db.OpenAsync();
 await new SqlCommand("""
 IF OBJECT_ID('dbo.Duties') IS NULL CREATE TABLE dbo.Duties (Day date PRIMARY KEY, OwnerId bigint NOT NULL, BackupId bigint NOT NULL, Completed bit NOT NULL DEFAULT 0, LastSlot varchar(32) NULL);
 IF OBJECT_ID('dbo.Rotation') IS NULL CREATE TABLE dbo.Rotation (ChatId bigint PRIMARY KEY, Used bit NOT NULL DEFAULT 0);
 IF OBJECT_ID('dbo.BotState') IS NULL CREATE TABLE dbo.BotState (Id int PRIMARY KEY, NextOffset bigint NOT NULL);
 IF NOT EXISTS (SELECT 1 FROM dbo.BotState WHERE Id=1) INSERT dbo.BotState VALUES (1,0);
 """, db).ExecuteNonQueryAsync();
}
var jobs = app.Services.GetRequiredService<IRecurringJobManager>();
jobs.AddOrUpdate<DutyWorker>("duty-tick", w => w.Tick(), "* * * * *");
jobs.AddOrUpdate<DutyWorker>("telegram-inbox", w => w.Inbox(), "* * * * *");
app.MapGet("/", () => Results.Text("FlatDuty is running. Use Telegram to confirm your duty."));
app.Run();

public record Member(string Name, long ChatId);
public record DutyConfig(string Connection, string Token, Member[] Members, TimeOnly Cutoff);
public class DutyWorker(DutyConfig cfg, HttpClient http) {
 private static DateTime LocalNow => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Kolkata"));
 private async Task<SqlConnection> Open() { var db = new SqlConnection(cfg.Connection); await db.OpenAsync(); return db; }
 private static SqlCommand Cmd(SqlConnection db, string sql, params (string,object)[] args) { var c = new SqlCommand(sql,db); foreach(var (k,v) in args) c.Parameters.AddWithValue(k,v); return c; }
 private async Task<JsonDocument> Bot(string method, object body) { using var r = await http.PostAsJsonAsync($"https://api.telegram.org/bot{cfg.Token}/{method}",body); if(!r.IsSuccessStatusCode) throw new Exception($"Telegram {method} failed: {(int)r.StatusCode}"); var json=JsonDocument.Parse(await r.Content.ReadAsStringAsync()); if(!json.RootElement.GetProperty("ok").GetBoolean()) {json.Dispose(); throw new Exception("Telegram rejected request");} return json; }
 private async Task Send(long id,string message,string? day=null) { object? markup = day is null ? null : new { inline_keyboard = new[]{new[]{new {text="Waste disposed ✓",callback_data="done:"+day}}}}; using var result=await Bot("sendMessage",new {chat_id=id,text=message,reply_markup=markup}); }
 [DisableConcurrentExecution(120)]
 public async Task Tick() {
  var now=LocalNow; var t=TimeOnly.FromDateTime(now); var tomorrow=now.Date.AddDays(1);
  bool evening=(tomorrow.DayOfWeek is DayOfWeek.Thursday or DayOfWeek.Sunday) && t>=new TimeOnly(21,0);
  bool morning=(now.DayOfWeek is DayOfWeek.Thursday or DayOfWeek.Sunday) && t>=new TimeOnly(6,30) && t<cfg.Cutoff;
  if(!evening && !morning) return;
  var day=evening?tomorrow:now.Date; var key=day.ToString("yyyy-MM-dd");
  var slot=evening?"evening":$"morning-{((int)(t-new TimeOnly(6,30)).TotalMinutes)/5}";
  await using var db=await Open();
  await using(var tx=(SqlTransaction)await db.BeginTransactionAsync(System.Data.IsolationLevel.Serializable)) {
   async Task<object?> Q(string sql,params (string,object)[] args) { using var c=Cmd(db,sql,args); c.Transaction=tx; return await c.ExecuteScalarAsync(); }
   if(await Q("SELECT OwnerId FROM dbo.Duties WHERE Day=@d",("@d",day)) is null) {
    foreach(var m in cfg.Members) await Q("IF NOT EXISTS(SELECT 1 FROM dbo.Rotation WHERE ChatId=@id) INSERT dbo.Rotation VALUES(@id,0)",("@id",m.ChatId));
    var eligible=new List<long>(); foreach(var m in cfg.Members) if(Convert.ToBoolean(await Q("SELECT Used FROM dbo.Rotation WHERE ChatId=@id",("@id",m.ChatId)))==false) eligible.Add(m.ChatId);
    if(eligible.Count==0) { foreach(var m in cfg.Members) await Q("UPDATE dbo.Rotation SET Used=0 WHERE ChatId=@id",("@id",m.ChatId)); eligible=cfg.Members.Select(m=>m.ChatId).ToList(); }
    var owner=eligible[RandomNumberGenerator.GetInt32(eligible.Count)]; var backups=cfg.Members.Where(m=>m.ChatId!=owner).ToArray(); var backup=backups[RandomNumberGenerator.GetInt32(backups.Length)].ChatId;
    await Q("UPDATE dbo.Rotation SET Used=1 WHERE ChatId=@id",("@id",owner));
    await Q("INSERT dbo.Duties(Day,OwnerId,BackupId) VALUES(@d,@o,@b)",("@d",day),("@o",owner),("@b",backup));
   }
   await tx.CommitAsync();
  }
  long ownerId,backupId; using(var c=Cmd(db,"SELECT OwnerId,BackupId,Completed,LastSlot FROM dbo.Duties WHERE Day=@d",("@d",day))) { await using var r=await c.ExecuteReaderAsync(); await r.ReadAsync(); if(r.GetBoolean(2)||(!r.IsDBNull(3)&&r.GetString(3)==slot)) return; ownerId=r.GetInt64(0); backupId=r.GetInt64(1); }
  var target=morning && t>=new TimeOnly(7,0)?backupId:ownerId;
  var msg=evening?$"You have waste duty tomorrow ({key}). Please prepare wet and dry waste tonight.":$"Waste collection today ({key}). {(target==backupId?"Backup duty: please help. ":"")}Confirm only after disposal.";
  if(Convert.ToBoolean(await Cmd(db,"SELECT Completed FROM dbo.Duties WHERE Day=@d",("@d",day)).ExecuteScalarAsync())) return;
  await Send(target,msg,key);
  await Cmd(db,"UPDATE dbo.Duties SET LastSlot=@s WHERE Day=@d",("@s",slot),("@d",day)).ExecuteNonQueryAsync();
 }
 [DisableConcurrentExecution(120)]
 public async Task Inbox() {
  await using var db=await Open(); var offset=Convert.ToInt64(await Cmd(db,"SELECT NextOffset FROM dbo.BotState WHERE Id=1").ExecuteScalarAsync());
  using var json=await Bot("getUpdates",new {offset,timeout=0,allowed_updates=new[]{"callback_query","message"}});
  foreach(var update in json.RootElement.GetProperty("result").EnumerateArray()) {
   if(update.TryGetProperty("callback_query",out var cb)) {
    var id=cb.GetProperty("from").GetProperty("id").GetInt64(); var data=cb.GetProperty("data").GetString()??""; var reply="Not authorized or invalid duty.";
    if(cfg.Members.Any(m=>m.ChatId==id)&&data.StartsWith("done:")&&DateTime.TryParseExact(data[5..],"yyyy-MM-dd",null,System.Globalization.DateTimeStyles.None,out var day)&&day==LocalNow.Date) {
     var changed=await Cmd(db,"UPDATE dbo.Duties SET Completed=1 WHERE Day=@d AND Completed=0 AND (OwnerId=@id OR BackupId=@id)",("@d",day),("@id",id)).ExecuteNonQueryAsync(); reply=changed==1?"Completed. Reminders stopped.":"Already completed or not your duty.";
    }
    using var ack=await Bot("answerCallbackQuery",new {callback_query_id=cb.GetProperty("id").GetString(),text=reply});
   }
   await Cmd(db,"UPDATE dbo.BotState SET NextOffset=@o WHERE Id=1",("@o",update.GetProperty("update_id").GetInt64()+1)).ExecuteNonQueryAsync();
  }
 }
}
