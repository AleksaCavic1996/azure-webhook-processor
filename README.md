# Azure Webhook Processor

[![CI](https://github.com/AleksaCavic1996/azure-webhook-processor/actions/workflows/ci.yml/badge.svg)](https://github.com/AleksaCavic1996/azure-webhook-processor/actions/workflows/ci.yml)

An idempotent payment webhook processor built with .NET and Azure Service Bus.
The same event delivered several times changes state only once.

It runs on Azure Container Apps with Azure Service Bus, PostgreSQL Flexible Server and Key Vault. The infrastructure is defined in Bicep, and every push to `main` is tested and deployed by GitHub Actions through OIDC.

There are no passwords or access keys anywhere in the system. The services sign in to Service Bus and PostgreSQL with managed identities, the database has no public endpoint, and the pipeline holds no credentials. The one remaining secret is the HMAC key shared with the webhook provider, kept in Key Vault.

## Architecture

```mermaid
flowchart LR
    provider([Payment provider]) -- "signed POST" --> api
    github([GitHub Actions]) -- "OIDC, new image" --> apps

    subgraph azure [Azure resource group]
        queue[["Service Bus queue<br/>payment-events + dead-letter"]]
        vault["Key Vault<br/>signing secret"]
        insights["Application Insights<br/>traces, alerts"]

        subgraph vnet [Virtual network]
            subgraph apps [Container Apps subnet]
                api["API<br/>public HTTPS"]
                worker["Worker<br/>no ingress"]
            end
            subgraph dbnet [PostgreSQL subnet]
                db[("PostgreSQL<br/>no public endpoint")]
            end
        end

        api -- "send (identity)" --> queue
        queue -- "receive (identity)" --> worker
        worker -- "one transaction (identity)" --> db
        vault -. "secret via identity" .-> api
        api -. "OpenTelemetry" .-> insights
        worker -. "OpenTelemetry" .-> insights
    end
```

## Design

1. `POST /webhooks/payments` validates an HMAC signature, enqueues the event and returns `202 Accepted`. It returns `401` for a bad signature, `400` for an invalid payload and `503` if the queue is unavailable.
2. A worker consumes the queue and, in one transaction, records the event and updates the payment. The message is completed only after the commit.
3. Delivery is at-least-once, and the effect is applied once per `eventId`. `INSERT ... ON CONFLICT DO NOTHING` on `processed_events` detects duplicates, including concurrent ones.
4. A failed delivery is retried after 2, 4, 8 and 16 seconds, and goes to the dead-letter queue after the fifth attempt. A message that can never succeed goes there immediately.

Known limitation: the retries span about 30 seconds of pauses, so a longer database outage moves messages to the dead-letter queue. They are not lost: an alert reports them, and `tools/DeadLetterReplay` moves them back to the queue once the cause is fixed (see [Operations](#operations)). Replaying is safe because processing is idempotent.

## Repository layout

| Path | Purpose |
| --- | --- |
| `src/WebhookProcessor.Api` | Webhook endpoint |
| `src/WebhookProcessor.Worker` | Queue consumer |
| `src/WebhookProcessor.Core` | Shared contracts |
| `tests/WebhookProcessor.Tests` | Tests |
| `tools/DeadLetterReplay` | Operator tool that replays dead-lettered messages |
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

Requires Docker for the integration and end-to-end tests. The unit and API tests run without it:

```
dotnet test -- --filter-namespace "WebhookProcessor.Tests.Unit" --filter-namespace "WebhookProcessor.Tests.Api"
```

The whole suite:

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
4. On `main`, the `deploy` job then rolls those images out to Azure (see [Deployment](#deployment)).

## Azure

Everything lives in one resource group and is created from `infra/main.bicep`.

| Resource | Role | Notes |
| --- | --- | --- |
| Container Apps (consumption profile) | Runs the API and the worker | Both scale to zero. The API starts on an HTTP request, the worker when messages are waiting (KEDA Service Bus rule). The environment is placed in the virtual network. |
| Service Bus, Basic tier | Queue between the API and the worker | One queue with the same settings as the local emulator. Basic is enough: no topics, sessions or broker-side duplicate detection are used. Shared access keys are disabled. |
| PostgreSQL Flexible Server, B1ms | Payments and the processed-event log | Same major version as local. In its own delegated subnet, with no public endpoint and password sign-in disabled. |
| Virtual network + private DNS zone | Private path from the apps to the database | One subnet for Container Apps, one for PostgreSQL. |
| Key Vault | The webhook signing secret | RBAC authorization, one role assignment on that one secret. |
| Managed identities | Every sign-in in the system | One each for the API, the worker, the autoscaler and the pipeline. |
| Application Insights + Log Analytics | Traces, metrics and logs | Fed by OpenTelemetry. |
| Azure Monitor alerts | Email when something needs a person | Dead-lettered messages, queue backlog, API 5xx. |

### Security

- **No passwords or keys to steal.** The API and the worker sign in to Service Bus with their managed identities, and the worker signs in to PostgreSQL with a Microsoft Entra access token. Shared access keys on Service Bus and password sign-in on PostgreSQL are switched off, so those credentials do not exist.
- **Least privilege per identity, on one queue.** The API identity can only send, the worker identity can only receive. Reading the queue length needs the owner role, so the autoscaler has a separate identity for that.
- **The database is not on the internet.** It sits in a delegated subnet with public access disabled. Only the container apps in the same virtual network can reach it.
- **One secret, tightly scoped.** The HMAC secret shared with the webhook provider is generated by `infra/deploy.ps1`, stored in Key Vault and readable only by the API identity.
- **The worker has no ingress.** Only the API is reachable from the internet.
- **No credentials in the repository, the images or the pipeline.**

### Deployment

Infrastructure and application are deployed separately, with different privileges.

**Infrastructure** is deployed by a person with rights on the subscription:

```powershell
az login
./infra/deploy.ps1            # add -WhatIf to preview
```

**Application** is deployed by the `deploy` job in CI on every push to `main`:

1. It signs in to Azure with OIDC. GitHub presents a short-lived token, and Azure accepts it only for this repository's `main` branch. There is no client secret to leak or rotate.
2. It updates both container apps to the image tagged with the commit SHA.
3. It waits for `/health/ready` on the new API revision and checks that both apps run that exact image.

The pipeline identity may change the two container apps and nothing else. It cannot reach the database, Service Bus, Key Vault or role assignments.

![GitHub Actions run with tests, image publishing and the deployment to Azure](docs/images/pipeline.png)

Send a signed test event to the deployed API:

```powershell
./scripts/send-test-event.ps1 -EventId evt-1 -PaymentId pay-1 -KeyVaultName <vault> -Url https://<api-host>/webhooks/payments
```

### Observability

Both services export traces, metrics and logs through OpenTelemetry to Application Insights. Export is on only where `APPLICATIONINSIGHTS_CONNECTION_STRING` is set, so local runs and tests send nothing.

The Service Bus client carries the trace context inside the message, so one event is one trace: the HTTP request, the send to the queue, the processing in the worker, each SQL command and the message completion. Log lines from both services carry the same trace id. Health probe requests are filtered out.

One event in Application Insights, from the HTTP request to the database:

![End-to-end trace of one webhook event](docs/images/trace.png)

The API answers `202` and sends the message, the worker processes it with two SQL commands (record the event, update the payment) and completes the message. The long bar is not processing time: it is the worker's receive call, which had been waiting on the empty queue for 37 seconds when this message arrived.

The application map built from the same telemetry:

![Application map: API, worker and PostgreSQL](docs/images/application-map.png)

### Operations

Three Azure Monitor alerts email the operator, and each one answers a single question:

| Alert | Question | Fires when |
| --- | --- | --- |
| `alert-webhook-dead-letters` | Did an event fail for good? | Any message is in the dead-letter queue |
| `alert-webhook-queue-backlog` | Is the worker keeping up? | More than 100 messages wait for 15 minutes |
| `alert-webhook-api-5xx` | Are we rejecting valid webhooks? | The API answers with any 5xx |

Dead-lettered messages are handled with `tools/DeadLetterReplay`. It signs in as the current Azure CLI user, who needs the `Azure Service Bus Data Owner` role on the queue (the deploying user gets it from the template).

```powershell
# List what is in the dead-letter queue, with the reason for each message. Changes nothing.
dotnet run --project tools/DeadLetterReplay -- --namespace <name>.servicebus.windows.net --dry-run

# After the cause is fixed: move the messages back to the queue.
dotnet run --project tools/DeadLetterReplay -- --namespace <name>.servicebus.windows.net
```

The tool sends a message before it removes it from the dead-letter queue, so an interrupted run can only produce a duplicate, and the worker already records duplicates without applying them twice.

### Cost

An idle system costs close to nothing: both apps scale to zero, Service Bus Basic is billed per operation, the virtual network is free, and the B1ms database is covered by the Azure free offer for 12 months. Deleting the resource group removes everything:

```
az group delete -n rg-webhook-processor
```

## Trade-offs

| Decision | Why | What it costs |
| --- | --- | --- |
| Idempotency in the database, not in the broker | Works on the cheapest Service Bus tier and is covered by tests against real PostgreSQL | One extra insert per event |
| Scale to zero | No cost while idle | The first request after idle waits a few seconds for a cold start. Webhook providers retry, so this is acceptable. |
| Managed identities instead of connection strings | Nothing to leak, rotate or store | The services depend on `Azure.Identity`, and local runs use a different sign-in path (connection strings to the emulator and a local database) than Azure does. |
| Database in a private subnet (VNet integration) | No public endpoint, and it costs nothing extra | Nobody can connect from a laptop. Inspecting data needs a jump host or a temporary app inside the network, and the network mode cannot be changed without recreating the server. |
| Worker identity is the database administrator | Nothing outside the network can run SQL to create a narrower role, so the identity is registered through Azure | The worker has more rights on the server than it needs for normal processing. The server hosts only this database, and the worker needs schema rights for migrations anyway. |
| Service Bus keeps a public endpoint | Private endpoints need the Premium tier, far beyond this workload | The namespace is reachable from the internet, protected by Entra sign-in and role assignments only. |
| Bicep | No state file and no extra tooling beyond the Azure CLI | Azure only |
| Pipeline deploys images only | A compromised pipeline cannot read secrets or delete infrastructure | Infrastructure changes are a manual step |
