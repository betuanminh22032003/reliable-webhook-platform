# ADR: Transactional outbox

- Status: Accepted
- Date: 2026-07-26

## Context

The MVP needs durable, understandable webhook processing without an external broker.

## Decision

Create events deliveries and outbox rows atomically to eliminate dual-write loss.

## Consequences

The design is operationally simple and inspectable. Database capacity bounds throughput, and receivers must tolerate duplicates.
