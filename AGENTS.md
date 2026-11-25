# FlatDuty project instructions

FlatDuty manages shared responsibilities for six flatmates. The first workflow covers municipal wet and dry waste collection. Missed pickups leave waste in the flat and attract flies. Measure success by whether a flatmate confirms they physically handed over the waste. Prefer free, low-friction software that requires no hardware.

## Waste collection workflow

All scheduled times use India Standard Time (Asia/Kolkata).

| When | Bot behavior |
| --- | --- |
| Wednesday and Saturday at 21:00 | Assign one flatmate to the next morning’s collection. Use fair random rotation across all six members and persist assignment history. |
| Thursday and Sunday from 06:30 | Remind the assigned flatmate every five minutes while the duty is open. |
| From 07:00 on collection days | If disposal is still unconfirmed, escalate to the backup flatmate. |
| On authenticated confirmation | Mark the duty complete and immediately stop reminders to both flatmates. |
| At the configurable cutoff | Stop reminders even if disposal remains unconfirmed. |

Persist duty state so the bot can recover after a restart and honor both completion and cutoff.

## Technology and scope

Use ASP.NET Core on .NET 10, Hangfire, SQL Server, and the Telegram Bot API. Use Docker and Docker Compose for deployment on a GCP VM, with GitHub Actions for automation. Validate the bot workflow before adding a frontend or introducing microservices.

## Development practices

- Work on `feature/` branches and make small, logical commits.
- Keep credentials and real `.env` files out of Git. Maintain a sanitized `.env.example` with placeholders.
- Document architectural decisions, implementation plans, and changes. Run the build and relevant tests before marking a feature complete.
- Independently identify failure scenarios, propose improvements, and implement practical fixes. Prefer the simplest design that reliably helps the household complete the duty.

## Next tasks

Add an injectable clock and automated tests, configurable scheduling and a test mode, flatmate onboarding, a missed-duty summary, and a reliable notification outbox with monitoring.
