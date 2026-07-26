# Progress

## Completed work

- Implemented all seven milestones: modular projects, domain/application policies, Dapper/PostgreSQL persistence and migrations, HTTP API, leased BackgroundService worker, tests, containers/CI, README, and five ADRs.
- Corrected Dapper materialization for PostgreSQL `timestamptz` and delivery-status values after exercising the persistence layer against PostgreSQL.
- Expanded integration coverage into seven focused PostgreSQL/API tests and added an external PostgreSQL mode so CI uses a deterministic healthy service container while local runs continue to use Testcontainers.
- Release restore and build pass with zero warnings; all eight unit tests and all seven integration tests pass against PostgreSQL 16.
- Docker Compose configuration validates successfully, and its API image explicitly installs the health-check client used by Compose.
- Reviewed the repository for generated artifacts and secrets; ignore rules exclude build output and `.env`.

## Current milestone

- Complete. All implementation and verification available in this environment has passed.

## Remaining work

- Publishing a public hosted demo requires deployment infrastructure and credentials, which are intentionally not part of this repository. The local demo is available at `http://localhost:8080` after `docker compose up --build`.

## Discovered risks

- This execution environment cannot start a Docker daemon because container-level privileges are unavailable. Compose configuration and the complete application were instead verified using the installed PostgreSQL service and host-run .NET processes.

## Failed approaches

- The original integration suite could not execute without Docker and concealed Dapper constructor/materialization failures. An external-connection test mode exposed and verified the fixes against real PostgreSQL.
- Initial restore rejected a vulnerable transitive OpenAPI package under warnings-as-errors; the unused OpenAPI package was removed.
- Initial unit tests exposed an overflow in large retry exponents and an incorrect known HMAC vector; both were corrected and rerun.

## Next action

- Push the current branch to run the corrected GitHub Actions workflow. For a public demo, deploy the existing Compose stack to a Docker-capable host and configure a DNS name/TLS reverse proxy.
