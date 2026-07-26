# Reliable Webhook Platform — Project Plan

## Architecture decisions

- Modular monolith with Domain → Application → Infrastructure dependency flow and two composition roots (API and Worker).
- PostgreSQL is both the system of record and durable delivery queue; Dapper and explicit SQL keep persistence transparent.
- Event, delivery, and outbox creation occur in one transaction. Workers claim short leases with `FOR UPDATE SKIP LOCKED` and never hold a transaction during HTTP calls.
- Delivery is at-least-once; consumers use the stable delivery identifier for deduplication.

## Implementation milestones and task checklist

- [x] 1. Establish solution structure, build policy, domain model, and application contracts.
- [x] 2. Implement PostgreSQL migrations and Dapper persistence, transactional publishing, claiming, and state updates.
- [x] 3. Implement API endpoints, validation, health/readiness, and payload limits.
- [x] 4. Implement delivery worker, HMAC signing, timeouts, retry/backoff, and graceful shutdown.
- [x] 5. Add meaningful unit, PostgreSQL Testcontainers, and API integration tests.
- [x] 6. Add Docker images, Compose stack, environment configuration, and GitHub Actions CI.
- [x] 7. Complete README, ADRs, security guidance, formatting, and final verification.

## Verification commands

```bash
dotnet format --verify-no-changes
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
docker compose config
```

## Definition of Done

- [x] All endpoint registration, publishing, inspection, health, and readiness routes work and validate input.
- [x] Versioned migrations provide keys, constraints, indexes, transactional outbox, and concurrency-safe claims.
- [x] Worker signs and sends deliveries, records attempts, retries failures, and terminally fails exhausted work.
- [x] Idempotency is database-enforced and concurrency-safe.
- [x] Unit and integration test suites pass, including API and real PostgreSQL coverage.
- [x] API and Worker start; Docker Compose validates and contains health checks.
- [x] CI, operational configuration, README, and five required ADRs are complete and accurate.
- [x] Mandatory restore, Release build, and Release test commands pass with no required TODOs or committed artifacts/secrets.
- [x] Final diff is reviewed and the implementation is committed.
