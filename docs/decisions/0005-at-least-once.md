# ADR: At-least-once semantics

- Status: Accepted
- Date: 2026-07-26

## Context

The MVP needs durable, understandable webhook processing without an external broker.

## Decision

Retry uncertain outcomes and expose stable delivery IDs so consumers can deduplicate.

## Consequences

The design is operationally simple and inspectable. Database capacity bounds throughput, and receivers must tolerate duplicates.
