# FlatDuty
A backend-first household waste collection assistant for six flatmates. Collection is Thursday and Sunday in India. The app assigns a fair randomized owner at 21:00 the previous evening, reminds every five minutes from 06:30, transfers reminders to a random backup from 07:00, and stops at confirmation or the configurable cutoff (default 08:00). Telegram is the interface; no hardware or paid voice API.

## Setup
1. Create a bot using Telegram @BotFather. Each member must open the bot and press Start. Before starting the service, use the Bot API getUpdates endpoint privately to obtain each private chat ID (message.chat.id). Never share the token or paste it into public logs. Clear any existing webhook first, because this app polls.
2. Provision an existing SQL Server database named FlatDuty and a login with permissions to create application and Hangfire tables. SQL Server is not included in Compose. Ensure the container can reach the server and validate its TLS certificate.
3. Copy .env.example to .env and fill in all six names, IDs, token, connection string and collection cutoff. Keep .env private.
4. Run `docker compose up -d --build` and inspect `docker compose logs -f`. The health text is available locally at http://localhost:8088/ (liveness only). No public inbound endpoint is required.
5. Keep the VM running. Telegram notifications must be enabled on each phone. Set recurring local phone alarms as an independent fallback.

## Current behavior and limits
- The assignment and rotation are persisted transactionally; missed evening jobs can recover in the morning. No backlog of obsolete reminder slots is sent.
- Confirmation is accepted from the assigned owner or backup on the collection day. Polling can take about a minute; a reminder already in flight may still arrive.
- Hangfire retries failed jobs. Delivery is at-least-once: a crash after Telegram accepts a message but before SQL records it can duplicate a reminder. No claim of exactly-once delivery.
- At cutoff the worker stops sending; it does not currently publish a missed-duty report. No dashboard, calendar integration, onboarding UI, or phone calls yet.
- Keep one bot consumer and one app deployment for this MVP. Do not change the member roster mid-cycle without reviewing rotation state.
- This source was generated in an environment without the .NET SDK or a live SQL/Telegram account; build and integration tests have not been run.

## Acceptance checks before relying on it
Build with `dotnet build` or Docker. In a disposable database, verify evening selection, six unique owners per rotation, morning reminders, backup escalation, authorized completion, rejection of another user's callback, restart recovery, cutoff and Telegram outage behavior. Test with temporary schedule changes before production use. Confirm actual collection time with flatmates.
