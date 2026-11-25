# FlatDuty
A backend-first household waste collection assistant for six flatmates. Collection is Thursday and Sunday in India. The app assigns a fair randomized owner at 21:00 the previous evening, reminds every five minutes from 06:30, transfers reminders to a random backup from 07:00, and stops at confirmation or the configurable cutoff (default 08:00). Telegram is the interface; no hardware or paid voice API.

## Run locally on Windows

1. Open PowerShell in the repository root. Keep your existing `.env`. For a fresh checkout, run `Copy-Item .env.example .env`. The app reads the key/value format shown in the example. Process environment variables take precedence.
2. Start the SQL Server instance. For a local default instance, connect to `localhost` in SSMS using Windows Authentication. In a query window, run `IF DB_ID(N'FlatDuty') IS NULL CREATE DATABASE [FlatDuty];`. The account running the app needs access to that database and permission to create the application tables and Hangfire schema. For a named instance, use the same server and instance name in SSMS and in `.env`.
3. Set `ConnectionStrings__Default` in `.env`. For a local default instance with a development certificate, the Windows example in `.env.example` uses `Integrated Security=True`. Run `dotnet run --no-launch-profile -- --check-db` to verify access. This opens the configured database and executes `SELECT 1` without starting jobs or contacting Telegram.
4. Create a bot with Telegram's @BotFather. Have each of the six flatmates open a **private chat** with the bot and press **Start**. While FlatDuty is stopped, use the Telegram Bot API's `getUpdates` method privately to obtain each person's `message.chat.id`. Set `BotToken`, the six `Members__N__Name` values and their six distinct positive `Members__N__ChatId` values in `.env` (indices 0 through 5). If the bot previously used a webhook, remove it before polling.
5. Run `dotnet run --no-launch-profile -- --urls http://localhost:5080`. Open <http://localhost:5080/> for the liveness message. Leave the app running for scheduled jobs. Stop with Ctrl+C.

## Run with Docker Compose

Compose starts the app and expects an existing SQL Server. Supply a reachable SQL host and SQL login in `.env`. On Docker Desktop, a SQL Server on the Windows host can be addressed as `host.docker.internal,<port>` when the instance accepts TCP connections and allows that login. Use a trusted SQL Server certificate for deployment. A Linux container does not inherit the Windows account used for a local run.

Run `docker compose up -d --build` and follow startup with `docker compose logs -f flatduty`. Open <http://localhost:8088/>. Keep only one instance polling the bot.

See the [development tracker](docs/development-tracker.md) for planned work.

## Current behavior and limits
- The assignment and rotation are persisted transactionally; missed evening jobs can recover in the morning. No backlog of obsolete reminder slots is sent.
- Confirmation is accepted from the assigned owner or backup on the collection day. Polling can take about a minute; a reminder already in flight may still arrive.
- Hangfire retries failed jobs. Delivery is at-least-once: a crash after Telegram accepts a message but before SQL records it can duplicate a reminder. No claim of exactly-once delivery.
- At cutoff the worker stops sending; it does not currently publish a missed-duty report. No dashboard, calendar integration, onboarding UI, or phone calls yet.
- Keep one bot consumer and one app deployment for this MVP. Do not change the member roster mid-cycle without reviewing rotation state.

## Acceptance checks before relying on it
Build with `dotnet build` or Docker. In a disposable database, verify evening selection, six unique owners per rotation, morning reminders, backup escalation, authorized completion, rejection of another user's callback, restart recovery, cutoff and Telegram outage behavior. Test with temporary schedule changes before production use. Confirm actual collection time with flatmates.
