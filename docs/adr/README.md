# Architecture Decision Records

ADRs record decisions that add to, or change, the Frozen Architecture
(`docs/architecture/frozen-architecture-v1.0.md`).

## When an ADR is required

- Weakening or changing a Frozen Architecture invariant — requires architectural reason, rejected
  alternatives, impact analysis and an ADR (Architecture Test Contract §28).
- Choosing between options the Frozen Architecture leaves open (e.g. ADR-0001 tenant isolation).
- Adding infrastructure outside the frozen baseline (e.g. a message broker) — must cite measured need.

## When an ADR is NOT required

- A human-approved **phase kickoff PR** that advances `docs/architecture/active-phase.json`, activates the
  next approved roadmap phase mandate and advances the A018 phase-scope data
  (`tests/Commercial.ArchitectureTests/PhaseScope/phase-scope.json`) — provided Frozen Architecture
  invariants are unchanged.

## Format

File name `ADR-NNNN-kebab-title.md`, with a `**Status:**` line using one of:
`PROPOSED`, `REQUIRED BEFORE PHASE n — NOT YET DECIDED`, `ACCEPTED — <summary>`, `SUPERSEDED BY ADR-NNNN`, `REJECTED`.

## Index

| ADR | Title | Status |
|---|---|---|
| [ADR-0001](ADR-0001-tenant-isolation-decision-gate.md) | Tenant isolation decision gate (EF filters vs PostgreSQL RLS vs hybrid) | REQUIRED BEFORE PHASE 1 — NOT YET DECIDED |
