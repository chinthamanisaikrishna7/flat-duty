# FlatDuty
A backend-first household waste collection assistant for six flatmates. Collection is Thursday and Sunday in India. The app assigns a fair randomized owner at 21:00 the previous evening, reminds every five minutes from 06:30, transfers reminders to a random backup from 07:00, and stops at confirmation or the configurable cutoff (default 08:00). Telegram is the interface; no hardware or paid voice API.

## Run locally on Windows

1. Open PowerShell in the checkout containing `MemberRegistration.cs`. Run `dotnet --version` and check for a 10.x SDK. To try the schedule immediately, run `dotnet run --no-launch-profile -- --simulate 2026-11-26 07:05`.
2. Open [BotFather in Telegram](https://t.me/BotFather), press **Start**, send `/newbot`, and follow the prompts. Save the **username** for invite links and the **token** for the application. Keep the token private.
3. Open SQL Server Management Studio (SSMS). Connect to `localhost` with **Windows Authentication** for a default local SQL Server instance. Open **New Query** and run `IF DB_ID(N'FlatDuty') IS NULL CREATE DATABASE [FlatDuty];`. The application creates its tables at startup. Use a Windows account with permission to create the application tables and Hangfire schema in this database. If SSMS cannot connect, check that **SQL Server (MSSQLSERVER)** is running in Windows Services. For a named instance, use its full server name in SSMS and the connection string.
4. Open the repository's `.env` in a local editor. For a fresh checkout, run `if (!(Test-Path .env)) { Copy-Item .env.example .env }` first. Enter the bot token as `BotToken`, replace the six `Members__N__Name` values with the flatmates’ names (including yours), and leave each `ChatId=0`. Existing installations with positive IDs can keep them. For SQL, use your existing connection string or the local Windows example in `.env.example`. Keep a single active `ConnectionStrings__Default` line.
5. Run `dotnet run --no-launch-profile -- --check-db`. Wait for `Connected to the configured SQL Server database.` before continuing.
6. Run `dotnet run --no-launch-profile -- --invites YourBotUsernameBot`, replacing the example with the username from step 2. FlatDuty prints a personal link beside each unregistered name. Send the matching link privately to each flatmate and keep yours. Re-running this command replaces **all outstanding** links. Flatmates who already joined stay registered.
7. Run `dotnet run --no-launch-profile -- --urls http://localhost:5080`. Leave the terminal open. Each flatmate opens their link in Telegram and presses **Start**. Within about a minute the bot should reply with the registered name and how many people have joined. Sending `/start` again shows the current count. Check for **6/6**. Scheduled duties start once all six have joined and an assignment or reminder window is open.
8. Open <http://localhost:5080/> to check whether the app is running. The laptop and SQL Server must stay awake for scheduled reminders. Use Ctrl+C to stop the app. Registrations survive restarts. A new bot can poll immediately. If reusing a bot, remove any existing webhook and stop any other poller first.

### How enrollment works

While the bot runs, use a second terminal in the project folder and run `dotnet run --no-launch-profile -- --registrations` to list each configured name and its registration status. This read-only command leaves existing links valid. On Windows, if the running app locks the executable, run `dotnet bin/Debug/net10.0/FlatDuty.dll --registrations` with the already-built version containing this command.

Six numbered slots correspond to the names in configuration. SQL stores the hash of each outstanding invitation and the Telegram chat ID once claimed. A private **Start** binds one account to one slot. A repeated Start returns that person's registration status. Keep the slot order stable after enrollment so names continue to match their owners.

## Run with Docker Compose

Compose starts the app and expects an existing SQL Server. Supply a reachable SQL host and SQL login in `.env`. On Docker Desktop, a SQL Server on the Windows host can be addressed as `host.docker.internal,<port>` when the instance accepts TCP connections and allows that login. Use a trusted SQL Server certificate for deployment. A Linux container does not inherit the Windows account used for a local run.

Run `docker compose up -d --build` and follow startup with `docker compose logs -f flatduty`. Open <http://localhost:8088/>. Keep only one instance polling the bot.

See the [development tracker](docs/development-tracker.md) for planned work.

## Simulate a collection day

Run this from the repository root with the .NET 10 SDK. The simulation uses the worker’s schedule and prints times in IST. It runs locally without SQL, Hangfire, a bot token, or any messages to flatmates.

```powershell
dotnet run --no-launch-profile -- --simulate 2026-11-26 07:05
```

This shows Wednesday’s 21:00 assignment, Thursday’s owner reminders from 06:30, the first backup reminder at 07:00, and confirmation at 07:05. Omit the confirmation time to see the complete timeline through cutoff. The preview models an authorized confirmation at the supplied time. The live bot processes confirmations when it polls Telegram.

To change the schedule, edit the optional `Schedule__CollectionDays`, `Schedule__AssignmentTime`, `Schedule__ReminderStart`, `Schedule__BackupTime`, `Schedule__IntervalMinutes`, and `Cutoff` settings shown in `.env.example` and restart the app. Enter full weekday names and 24-hour times. The existing `Cutoff` setting still works. If backup starts between reminder slots, its first reminder is due at the backup time and its interval starts there.

## Automated tests

Run `dotnet test tests/FlatDuty.Tests`. Schedule tests run offline. For the database workflow test, set `FLATDUTY_TEST_SQL` to a **test SQL Server** connection string whose login can create databases. The test creates a uniquely named database, runs the production worker against it with a fake clock and simulated Telegram responses, then drops that database. It exercises restart recovery, rotation, reminder deduplication, escalation, completion authorization, and cutoff. Without that variable, the runner reports the database test as skipped.

The GitHub Actions test workflow provisions SQL Server for this check on feature-branch pushes, main pushes, and pull requests. Deployment is still planned.

## Telegram delivery and missed duties

Start the updated app once to create the outbox table in the configured database. The scheduler records the current notification slot and assigned recipient in the same SQL transaction as the duty. The worker claims that slot before contacting Telegram, then records the result. After a restart it can reclaim an interrupted attempt after two minutes if the slot is still current. A temporary failure gets up to three attempts per slot. A Telegram rate limit with a retry interval pauses duty sends across slots. A rejected recipient ends that slot. The regular backup schedule still applies.

Run `dotnet run --no-launch-profile -- --duty-report` to inspect the past seven calendar days and any assignment for tomorrow. It distinguishes confirmed disposal, unconfirmed duties after cutoff, and scheduled dates without an assignment. Delivery totals cover slots recorded since the outbox was installed. Unacknowledged slots can include expired or interrupted attempts. Only the current slot of an open duty can be sent. Historical dates without a duty are evaluated using the current schedule.

## Google Calendar fallback

Run `dotnet run --no-launch-profile -- --export-calendar` to write `bin/flatduty-backup.ics`. The file contains one recurring check per collection day at the configured backup time (Thursday and Sunday at 07:00 IST by default). It contains no account details and the exporter works without SQL or Telegram.

On a computer, open Google Calendar **Settings → Import & export → Import**, select the file, and choose a calendar. Each flatmate who wants the fallback can import it into their own calendar. Open the imported series, verify the local time, and set a notification at the event time for the series. Check that Google Calendar notifications are enabled on the device. [Import events](https://support.google.com/calendar/answer/37118) · [Notification settings](https://support.google.com/calendar/answer/37242).

Calendar runs this household check independently of FlatDuty and can still alert after someone confirms disposal. The event asks the household to check once. Owner assignment, escalation, and completion remain in Telegram. If the collection schedule changes, remove the old series and import a fresh export.

## Current behavior and limits
- The assignment and rotation are persisted transactionally; missed evening jobs can recover in the morning. No backlog of obsolete reminder slots is sent.
- Confirmation is accepted from the assigned owner or backup on the collection day. Polling can take about a minute; a reminder already in flight may still arrive.
- A crash between Telegram accepting a message and SQL recording the delivery can duplicate a reminder. An outage lasting past cutoff can prevent delivery altogether.
- At cutoff the worker stops sending. The duty report is available locally on demand.
- Keep one bot consumer and one app deployment for this MVP. Do not change the member roster mid-cycle without reviewing rotation state.

## Acceptance checks before relying on it
Before relying on the bot, run a collection cycle with a test bot and disposable database under Hangfire. Verify that the owner and backup receive the expected messages, that a real button press stops reminders, and that a Telegram outage can recover before cutoff. Confirm the actual collection time with flatmates.
