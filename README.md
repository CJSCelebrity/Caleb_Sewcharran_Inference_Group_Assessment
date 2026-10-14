# Employee 360 Integration

A small .NET 10 service that consumes `EmployeeUpdated` events from the HR system (D365), validates and normalises them, and delivers them to the Employee 360 API. The emphasis is on what happens when things go wrong.

## Run it

Prerequisite: .NET 10 SDK.

```bash
# terminal 1: mock downstream Employee 360 API (http://localhost:5200)
dotnet run --project src/Employee360.MockApi

# terminal 2: the integration service (http://localhost:5100)
dotnet run --project src/Employee360.Integration.Api

# tests
dotnet test
```

Then step through `samples/demo.http` (VS Code REST Client, Rider or Visual Studio). Without an IDE:

```bash
curl -X POST http://localhost:5100/simulate/events -H "Content-Type: application/json" --data-binary @samples/events/01-employee-updated.json
curl http://localhost:5100/events/EVT-10452-001
```

The integration stores its state in `employee-integration.db` (SQLite) in the directory you run it from. Delete the file to start clean.

## Projects

Clean Architecture: dependencies point inwards (Api → Infrastructure → Application → Domain).

| Project | Layer | Contents |
|---|---|---|
| `Employee360.Integration.Domain` | Domain | Entities (`EventRecord`, `DeadLetter`, `EmployeeState`), enums, `ValidEmployeeUpdate`. No dependencies |
| `Employee360.Integration.Application` | Application | `EmployeeEventProcessor`, validation, normalisation, source/target contracts, ports (`IApplicationDbContext`, `IEmployee360Client`, `IEventBus`) |
| `Employee360.Integration.Infrastructure` | Infrastructure | EF Core/SQLite, resilient Employee 360 HTTP client, in-memory bus and the `EventConsumer` hosted service |
| `Employee360.Integration.Api` | Presentation | Composition root (`Program.cs`) and the operational endpoints |
| `Employee360.MockApi` | Test double | Mock Employee 360. Honours `Idempotency-Key`; `/chaos` injects 5xx responses or slow responses |
| `tests/Employee360.Integration.Tests` | Tests | xUnit tests: idempotency, ordering, validation, dead-lettering, retries |

`IApplicationDbContext` exposes EF Core `DbSet`s rather than hand-written repositories, so Application references the EF Core abstractions package (not the SQLite provider). That is a deliberate, pragmatic trade-off: the processor needs only find/add/save, and EF Core already is a unit of work.

## How an event flows

```
POST /simulate/events ──► InMemoryEventBus ──► EventConsumer ──► EmployeeEventProcessor ──► Employee360Client ──► Employee 360
     (stands in for D365        (stands in for       (delivery count,      parse → de-dupe → validate →      (retry/backoff,
      publishing to Service      Service Bus queue)   redelivery,           normalise → order check →          timeouts, circuit
      Bus)                                            dead-letter)          map → deliver                       breaker)
```

| Scenario | Behaviour |
|---|---|
| Same event delivered twice / already processed | `eventId` is the idempotency key (primary key of `Events`). Terminal events are acknowledged as `Duplicate` and never re-sent. |
| Same `eventId`, different content | Dead-lettered as `ConflictingDuplicate`; the original is left untouched. |
| Older event after a newer one | Events are full snapshots, so an event with an `effectiveDate` no newer than the last delivered one is `SkippedStale` (recorded, not an error). |
| Missing employee ID / invalid fields | Dead-lettered as `ValidationFailed` with the list of errors. |
| Malformed JSON | Dead-lettered as `MalformedPayload`, raw body kept verbatim. |
| `CPT` vs `Cape Town` | Normalised via configured reference data. Unknown values are quarantined (`UnmappedReferenceData`), never passed through. |
| Downstream timeout / 5xx / 408 / 429 | Retried with exponential backoff and jitter (3 retries, 3s per-attempt timeout). If still failing, the message is redelivered (up to 3 deliveries) and then dead-lettered (`MaxDeliveryCountExceeded`). |
| Downstream 4xx | Permanent: dead-lettered immediately (`DownstreamRejected`), not retried. |
| Timeout after downstream actually applied the update | `eventId` is sent as `Idempotency-Key`, so the retry is recognised downstream and not applied twice. |

## Operational endpoints

| Endpoint | Use |
|---|---|
| `GET /events/{eventId}` | Status, attempts, duplicate count, last error, related dead letters |
| `GET /employees/{employeeId}` | Last state delivered for the employee and the events that touched it |
| `GET /dead-letters` | Most recent exception-store entries |
| `POST /dead-letters/{id}/replay` | Re-publish the original payload (e.g. after an outage or a reference-data fix) |
| `GET /status`, `GET /health` | Counts by status, queue depth, open dead letters; liveness |

## Configuration

Everything tunable is in `appsettings.json`: downstream base URL, retry and timeout settings (`Employee360:Resilience`), max deliveries and redelivery delay (`Bus`), and location aliases (`ReferenceData:Locations`). No secrets are needed locally. In Azure, the downstream call would authenticate through APIM using managed identity, and any remaining secrets would come from Key Vault.

## Deliberate simplifications (time-boxed)

- In-memory bus instead of Service Bus, so messages in flight are lost on restart. The `IEventBus` seam is where `ServiceBusProcessor` would plug in.
- A single consumer processes messages sequentially. Concurrent consumers would need Service Bus sessions keyed on `employeeId` to preserve per-employee ordering.
- SQLite with `EnsureCreated` instead of SQL Server/Cosmos with migrations.
- Location mapping lives in config. In OCS it would come from Profisee MDM.
- Ordering relies on `effectiveDate`, and ties are treated as stale (first one wins). A source-side version or sequence number (e.g. the Dataverse `versionnumber`) would be more robust.
- Replay sends the original payload as-is; there is no "edit and replay".

## Evolving this in the OCS environment

- **Ingress:** D365/Dataverse business events (or Dataverse webhooks) → **Event Grid** → **Service Bus** topic with duplicate detection enabled and **sessions keyed on employeeId** for per-employee ordering, with a subscription per downstream consumer.
- **Processing:** the processor runs in an **Azure Function** (Service Bus trigger, isolated worker), or in a Container App/App Service if it grows. Complete/Abandon/DeadLetter map directly onto the outcomes here, and Service Bus `MaxDeliveryCount` plus the built-in DLQ replace the in-memory equivalents.
- **Downstream:** Employee 360 sits behind **API Management**, which handles auth (managed identity / OAuth), throttling and versioning, so consumers don't depend on its location.
- **Idempotency store:** Azure SQL or Cosmos DB (eventId as the key, TTL for housekeeping).
- **Security:** managed identity everywhere, and **Key Vault** for anything that can't use it.
- **Operations:** **Application Insights** with the correlation id propagated end to end, alerts on DLQ depth and on failure rate. A **Logic App** handles DLQ triage (notify the data steward, approve, replay) where human workflow is needed.
- **Data platform:** delivered events also land in the lake for Databricks/dbt, so Employee 360 data can be reconciled against source.

## Build pipeline

There are two equivalent CI definitions, depending on where the repo is hosted:

- **GitHub Actions** (`.github/workflows/ci.yml`): restores, builds and tests on every push and PR to `main`, uploads the test results, and on `main` publishes the API as a build artifact.
- **Azure DevOps** (`azure-pipelines.yml`): the same build-and-test stages, publishing results to the pipeline's Tests tab.

Deployment stages (infrastructure as code, deploy to Function app/App Service per environment with approvals) would follow.
