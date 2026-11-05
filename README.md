# Azure Webhook Processor

An idempotent payment webhook processor built with .NET and Azure Service Bus.
The same event delivered several times changes state only once.

**Status:** work in progress. The solution skeleton and local infrastructure exist; the service is not implemented yet.

## Planned design

1. `POST /webhooks/payments` validates an HMAC signature, enqueues the event and returns `202 Accepted`.
2. A worker consumes the queue and applies each event to PostgreSQL exactly once per `eventId`.
3. Failed messages are retried, and poison messages end up in the dead-letter queue.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/WebhookProcessor.Api` | Webhook endpoint |
| `src/WebhookProcessor.Worker` | Queue consumer |
| `src/WebhookProcessor.Core` | Shared contracts |
| `tests/WebhookProcessor.Tests` | Tests |
| `infra/` | Infrastructure as code |
| `.github/workflows/` | CI/CD pipelines |

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

4. Build the solution:

   ```
   dotnet build
   ```

| Service | Address |
| --- | --- |
| PostgreSQL | `localhost:5432` |
| Service Bus emulator (AMQP) | `localhost:5672` |
| Service Bus emulator (management, health) | `localhost:5300` |

The emulator does not persist messages or entities across container restarts. The queue is recreated from `servicebus-config.json` on startup.
