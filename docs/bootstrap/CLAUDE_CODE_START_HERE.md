# Claude Code — Phase 0 START HERE

Implement **only the active Commercial Platform phase**. For this package the active phase is expected to be Phase 0 — Foundation.

## 1. Read before changing code

Read in this exact order:

1. `docs/architecture/frozen-architecture-v1.0.md`
2. `CLAUDE.md`
3. `docs/architecture/active-phase.json`
4. the mandate referenced by `active-phase.json` (currently `PHASE0_IMPLEMENTATION_MANDATE.md`)
5. `ARCHITECTURE_TEST_CONTRACT.md`
6. `docs/adr/ADR-0001-tenant-isolation-decision-gate.md`

Do not begin implementation until you have read all six.

## 2. First response / repository assessment

Before editing, report briefly:

- detected active phase,
- existing repository structure,
- missing Phase 0 deliverables,
- any contradiction with Frozen Architecture,
- proposed Phase 0-only implementation sequence.

Do not ask to widen the scope if the repository is empty; create the required Phase 0 foundation.

## 3. Hard boundaries

- Legacy Sales Ops is not an architecture/domain source.
- Kumpara is an external system; do not reference Kumpara DB/internal assemblies.
- Do not implement a Kumpara connector in Phase 0.
- Do not implement Customer, Catalog, Pricing, Quote, Order or finance business workflows in Phase 0.
- Do not add RabbitMQ, microservices or Kubernetes assumptions.
- V1 async baseline is PostgreSQL Outbox + Hangfire.
- Do not invent external ERP contract tests in Phase 0.
- Do not choose production tenant isolation silently; leave the required session/context hooks and keep ADR-0001 as the Phase 1 decision gate.

## 4. Required implementation order

1. Create/repair solution, project and frontend skeletons.
2. Add central package/version management.
3. Make `docs/architecture/active-phase.json` available to architecture tests.
4. Implement the first executable architecture rules before business foundation expands.
5. Deliberately prove at least one architecture rule can fail, then restore it.
6. Set up PostgreSQL, Redis and Hangfire.
7. Implement transactional outbox with safe multi-worker row claiming and idempotent test handler.
8. Add error contract, correlation ID and observability.
9. Add the tenant-scope/session-initializer seams without choosing RLS vs EF filter.
10. Start API and Worker hosts.
11. Create admin and B2B frontend foundations.
12. Add Testcontainers integration tests.
13. Add Phase 0 contract tests: OpenAPI snapshot + error-envelope contract only.
14. Add CI gates.
15. Run the full Phase 0 acceptance checklist.

## 5. Provider-token warning

Do not implement broad source scans for `Logo` or `SAP`.

`CompanyLogo` and `LogoUrl` are legitimate names.

Use exact provider tokens defined in `ARCHITECTURE_TEST_CONTRACT.md`.

## 6. Phase scope guard

A018 must read `docs/architecture/active-phase.json`.

Do not permanently encode "Phase 0 forbidden forever".

A future phase kickoff PR is expected to change the active phase and the phase-specific A018 rules without weakening Frozen Architecture.

## 7. Completion report

Return a `PHASE 0 IMPLEMENTATION REPORT` using the exact normative format from `PHASE0_IMPLEMENTATION_MANDATE.md` §22:

- Active Phase
- Implemented
- Repository Structure
- Architecture Rules Enforced
- Deliberate Failure Rule Proven
- Tests Run / Results
- Outbox Multi-worker Test Result
- Contract Tests Result
- Tenant Isolation Readiness / ADR-0001 Status
- Runtime Validation
- CI Validation
- New Dependencies
- Migrations
- Deviations
- Deferred Work
- Final Gate: PASS / FAIL

If any required gate is missing or failing, report **FAIL / INCOMPLETE**. Never declare PASS based on partial completion.
