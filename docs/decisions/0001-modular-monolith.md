# ADR: Modular monolith

- Status: Accepted
- Date: 2026-07-26

## Context

The MVP needs durable, understandable webhook processing without an external broker.

## Decision

Keep domain rules independent while deploying API and worker together from one repository.

## Consequences

The design is operationally simple and inspectable. Database capacity bounds throughput, and receivers must tolerate duplicates.
