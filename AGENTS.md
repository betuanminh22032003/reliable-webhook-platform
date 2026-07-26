# Repository Agent Guidance

- Keep dependency directions documented in `PROJECT_PLAN.md`; do not add a generic repository or mediator layer.
- Prefer explicit SQL and small feature-focused services over speculative abstractions.
- Treat secrets and payloads as sensitive: never log signing secrets or full payloads.
- Use UTC timestamps (`DateTimeOffset`) and cancellation tokens for all I/O.
- Run `dotnet format`, restore, Release build, relevant tests, and update progress after each milestone.
- Do not commit generated `bin/`, `obj/`, test-result, IDE, or secret files.
