# Azure Webhook Processor

[![CI](https://github.com/AleksaCavic1996/azure-webhook-processor/actions/workflows/ci.yml/badge.svg)](https://github.com/AleksaCavic1996/azure-webhook-processor/actions/workflows/ci.yml)

An idempotent payment webhook processor built with .NET and Azure Service Bus.
The same event delivered several times changes state only once.

**Status:** work in progress. The service runs locally and in containers against the Service Bus emulator, with automated tests and CI. The Azure deployment is not done yet.

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

## Tests

Requires Docker for everything except the unit and API tests.

```
dotnet test
```

| Group | What it proves | Needs |
| --- | --- | --- |
| `Unit` | HMAC signature (checked against a value computed with openssl), replay window, payload validation | nothing |
| `Api` | Status codes 202, 400, 401 and 503, and that the exact signed bytes reach the queue. The broker is replaced by a recording fake. | nothing |
| `Integration` | Idempotency against real PostgreSQL: the same event three times, ten parallel deliveries of one event, redelivery after a crash before the message is completed, order-independent status, rollback on a currency mismatch | Docker (Testcontainers) |
| `EndToEnd` | Signed HTTP request to database row through the Service Bus emulator, ten parallel posts, a poison message dead-lettered without blocking the queue | Docker, about 1 minute to start the emulator |

Skip the slow end-to-end group:

```
dotnet test -- --filter-not-trait "Category=EndToEnd"
```

The end-to-end tests start the Service Bus emulator, which means accepting its license terms (linked in `.env.example`).

## Containers

Both services have a multi-stage Dockerfile. They build with the .NET SDK version from `global.json` and run on the chiseled ASP.NET image, which has no shell or package manager and runs as a non-root user.

Run the whole system in containers (set `WEBHOOK_SIGNING_SECRET` in `.env` first):

```powershell
docker compose --profile app up -d --build
./scripts/send-test-event.ps1 -EventId evt-1 -PaymentId pay-1 -FromEnvFile
```

The API container listens on `http://localhost:8080` and the worker's health endpoints on `http://localhost:8081`.

## CI

`.github/workflows/ci.yml` runs on every push to `main` and every pull request:

1. Release build with warnings treated as errors, then the unit, API and integration tests.
2. In parallel, the end-to-end tests against the Service Bus emulator.
3. After both pass, the `api` and `worker` images are built. On `main` they are pushed to GitHub Container Registry as `ghcr.io/aleksacavic1996/webhook-processor-api` and `-worker`, tagged with the commit SHA and `latest`. The push uses the workflow token, so no credentials are stored.
