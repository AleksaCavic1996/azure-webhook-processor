# Azure Webhook Processor

[![CI](https://github.com/AleksaCavic1996/azure-webhook-processor/actions/workflows/ci.yml/badge.svg)](https://github.com/AleksaCavic1996/azure-webhook-processor/actions/workflows/ci.yml)

An idempotent payment webhook processor built with .NET and Azure Service Bus.
The same event delivered several times changes state only once.

**Status:** work in progress. The core service runs locally against the Service Bus emulator. Automated tests, CI and the Azure deployment are not done yet.

## Design

1. `POST /webhooks/payments` validates an HMAC signature, enqueues the event and returns `202 Accepted`. It returns `401` for a bad signature, `400` for an invalid payload and `503` if the queue is unavailable.
2. A worker consumes the queue and, in one transaction, records the event and updates the payment. The message is completed only after the commit.
3. Delivery is at-least-once, and the effect is applied once per `eventId`. `INSERT ... ON CONFLICT DO NOTHING` on `processed_events` detects duplicates, including concurrent ones.
4. A failed delivery is retried after 2, 4, 8 and 16 seconds, and goes to the dead-letter queue after the fifth attempt. A message that can never succeed goes there immediately.

Known limitation: the retries span about 30 seconds of pauses, so a longer database outage moves messages to the dead-letter queue. They are not lost, and resending them is safe because processing is idempotent. Resending is a manual step for now.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/WebhookProcessor.Api` | Webhook endpoint |
| `src/WebhookProcessor.Worker` | Queue consumer |
| `src/WebhookProcessor.Core` | Shared contracts |
| `tests/WebhookProcessor.Tests` | Tests |
| `infra/` | Infrastructure as code |
| `.github/workflows/` | CI/CD pipelines |
| `scripts/` | Local helper scripts |

## Run locally

Requires the .NET 10 SDK and Docker Desktop.

1. Copy `.env.example` to `.env`, set both passwords, and set `ACCEPT_EULA=Y` if you accept the license terms linked in that file.
2. Start the infrastructure:

   ```
   docker compose up -d
   ```

3. Check that the Service Bus emulator is ready:

   ```
   curl http://localhost:5300/health
   ```

4. Store the local secrets outside the repository, using your `POSTGRES_PASSWORD` and any long random signing secret:

   ```
   dotnet user-secrets set "ConnectionStrings:Postgres" "Host=localhost;Port=5432;Database=webhooks;Username=webhooks;Password=<password>" --project src/WebhookProcessor.Worker
   dotnet user-secrets set "Webhook:SigningSecret" "<secret>" --project src/WebhookProcessor.Api
   ```

5. Start the API and the worker, each in its own terminal. In the Development environment, the worker applies database migrations on startup.

   ```
   dotnet run --project src/WebhookProcessor.Api --launch-profile http
   dotnet run --project src/WebhookProcessor.Worker
   ```

6. Send the same signed event three times, then check that the amount was added only once:

   ```powershell
   1..3 | ForEach-Object { ./scripts/send-test-event.ps1 -EventId evt-1 -PaymentId pay-1 -Amount 1500 }
   docker compose exec postgres psql -U webhooks -d webhooks -c "select * from payments"
   ```

| Service | Address |
| --- | --- |
| API | `http://localhost:5271` |
| Worker (health only) | `http://localhost:5272` |
| PostgreSQL | `localhost:5432` |
| Service Bus emulator (AMQP) | `localhost:5672` |
| Service Bus emulator (management, health) | `localhost:5300` |

Both the API and the worker expose `/health/live` (the process is running) and `/health/ready` (its dependencies are available).

The emulator does not persist messages or entities across container restarts. The queue is recreated from `servicebus-config.json` on startup.
