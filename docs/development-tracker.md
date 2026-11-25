# FlatDuty development tracker

8 of 15 tasks complete. Build and integration testing stays open until the collection workflow has passed an end-to-end test.

- [x] Initial project setup and Git history
- [x] Fair random duty assignment
- [x] Hangfire scheduling
- [x] Telegram reminders
- [x] Completion tracking
- [x] Backup escalation
- [x] Docker configuration
- [ ] Build and integration testing
- [ ] Telegram self-service registration
- [x] Test mode for simulating collection days
- [ ] Reliable notification outbox
- [ ] Google Calendar fallback
- [ ] GitHub Actions CI/CD
- [ ] GCP deployment
- [ ] Web dashboard (optional)

The simulation runs offline and uses the same IST schedule as the live worker. The automated test suite includes an opt-in SQL workflow test with a simulated Telegram API. GitHub Actions is configured to run it with a disposable SQL Server. The hosted run, a live Telegram acceptance test, and deployment are pending.
