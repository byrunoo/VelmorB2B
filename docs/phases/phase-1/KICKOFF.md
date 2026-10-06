# Phase 1 Kickoff Package (approved — not yet applied)

Nothing in this folder is active yet: `docs/architecture/active-phase.json` is still phase 0 and the enforced A018
data (`tests/Commercial.ArchitectureTests/PhaseScope/phase-scope.json`) is unchanged. The kickoff PR applies it.

## 1. Approvals

| Item | State |
|---|---|
| ADR-0001 tenant isolation | ACCEPTED — RLS-primary hybrid |
| ADR-0002 Phase 0 hosted-CI waiver | ACCEPTED — TEMPORARY WAIVER (hosted CI NOT PASS; D1–D7 required before commercial release) |
| `PHASE1_IMPLEMENTATION_MANDATE.md` | APPROVED — NOT YET ACTIVE |
| A014 activation model (Level A invariants + Level B acceptance suite) | APPROVED and implemented in the architecture tests (2026-10-07) |
| `scripts/db/shared-dev-role-split.sql` | DBA-reviewed prerequisite — **not executed**; executed in Phase 1 step 3 |
| **Phase 1 activation** | **awaiting human approval of the actual kickoff** |

## 2. Kickoff PR — exact changes (kickoff exemption only)

1. `docs/architecture/active-phase.json` ← content of `active-phase.proposed.json`:

   ```diff
   -  "phase": 0,
   -  "name": "Foundation",
   -  "displayName": "Phase 0 — Foundation",
   +  "phase": 1,
   +  "name": "Identity / Tenant / Security",
   +  "displayName": "Phase 1 — Identity / Tenant / Security",
      "status": "ACTIVE",
   -  "mandate": "docs/bootstrap/PHASE0_IMPLEMENTATION_MANDATE.md",
   +  "mandate": "docs/phases/phase-1/PHASE1_IMPLEMENTATION_MANDATE.md",
      ...
   -  "nextPhaseGate": "ADR-0001 must be ACCEPTED before the Phase 1 kickoff PR (...)"
   +  "acceptedDecisions": ["docs/adr/ADR-0001-...", "docs/adr/ADR-0002-..."],
   +  "previousPhase": { "phase": 0, "gate": "LOCAL PASS; hosted CI deferred by ADR-0002 (D1-D7 ...)" }
   ```

2. `tests/Commercial.ArchitectureTests/PhaseScope/phase-scope.json` ← add the `"1"` entry of
   `phase-scope.phase1.json` (same schema; the `"0"` entry stays for history).
3. Mandate status line → `APPROVED FOR IMPLEMENTATION — ACTIVE`.
4. `docs/phases/README.md` state column.

No functional code, no migration, no A014 code change (already done).

## 3. Dry-run evidence (2026-10-07, reverted afterwards)

| Run | Result |
|---|---|
| Proposed active phase 1 + phase-1 A018 data, **old** A014 (phase-number based) | 3 A014 failures (no marker / no-op initializer / proofs) — kickoff impossible |
| Same, **new** A014 (Level A, implementation-triggered) | architecture tests **86/86 PASS** — empty kickoff commit possible |
| Level A trigger proof: temporary `ITenantScoped` on an existing entity (phase 0) | 3 expected failures (missing RLS + TenantId, no-op initializer, missing core proofs) — triggers are live |

## 4. A014 enforcement model (approved)

- **Level A — implementation invariants, any phase**, triggered by the implementation itself:
  scoped entity ⇒ NOT NULL scope columns + `ENABLE`/`FORCE` RLS + USING/WITH CHECK policy; any isolation
  implementation (scoped entity, RLS DDL, production initializer type) ⇒ registered initializer is not the no-op +
  core proofs (`cross-tenant`, `cross-company`, `cross-customer`, `fail-closed`, `pool-scope-leak`,
  `runtime-role-not-owner`); always ⇒ no EF visibility filters, `No Reset On Close` forbidden.
- **Level B — Phase 1 acceptance:** the full mandate §20 suite and §24 gate, proven by the acceptance run and the
  Phase 1 implementation report. **No manual permanent "phase complete" flag.**

## 5. Kickoff checklist

- [x] ADR-0001 accepted · ADR-0002 accepted (temporary waiver) · mandate approved · A014 model approved/implemented
- [ ] Human approval of the actual kickoff
- [ ] `active-phase.json` replaced (mandate path valid)
- [ ] `phase-scope.json` contains the `"1"` entry
- [ ] mandate status → `APPROVED FOR IMPLEMENTATION — ACTIVE`
- [ ] `scripts/ci.ps1` green on the kickoff commit

## 6. Pre-conditions that remain for later Phase 1 steps (not kickoff blockers)

- Before the first real tenant-scoped migration reaches the shared dev DB: owner/runtime split in code, migration
  credential support, owner-driven Hangfire provisioning, DBA executes and verifies `shared-dev-role-split.sql`.
- Before commercial release: ADR-0002 D1–D7.
