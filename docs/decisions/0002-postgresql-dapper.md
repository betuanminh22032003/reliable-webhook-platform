# ADR: PostgreSQL and Dapper

- Status: Accepted
- Date: 2026-07-26

## Context

The MVP needs durable, understandable webhook processing without an external broker.

## Decision

Use PostgreSQL durability and constraints with Dapper and visible feature-specific SQL.

## Consequences

The design is operationally simple and inspectable. Database capacity bounds throughput, and receivers must tolerate duplicates.
