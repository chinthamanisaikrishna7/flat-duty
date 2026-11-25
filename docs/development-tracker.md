# FlatDuty development tracker

11 of 15 tasks implemented. Build and integration testing stays open until the collection workflow has passed an end-to-end test.

- [x] Initial project setup and Git history
- [x] Fair random duty assignment
- [x] Hangfire scheduling
- [x] Telegram reminders
- [x] Completion tracking
- [x] Backup escalation
- [x] Docker configuration
- [ ] Build and integration testing
- [x] Telegram self-service registration (personal invite links, pending a live household trial)
- [x] Test mode for simulating collection days
- [x] Reliable notification outbox (SQL integration run pending)
- [x] Google Calendar fallback (offline calendar export, household import pending)
- [ ] GitHub Actions CI/CD
- [ ] GCP deployment
- [ ] Web dashboard (optional)

The simulation runs offline and uses the same IST schedule as the live worker. The opt-in SQL workflow test covers registration, link reissue and replay, six-member activation, and the reminder cycle using a simulated Telegram API. GitHub Actions is configured to run it with a disposable SQL Server. The hosted run, a live Telegram acceptance test, and deployment are pending.
