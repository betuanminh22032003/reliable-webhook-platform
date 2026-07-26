# ADR: Database-backed delivery queue

- Status: Accepted
- Date: 2026-07-26

## Context

The MVP needs durable, understandable webhook processing without an external broker.

## Decision

Claim short leases with SKIP LOCKED; this avoids extra infrastructure at MVP scale.

## Consequences

The design is operationally simple and inspectable. Database capacity bounds throughput, and receivers must tolerate duplicates.
