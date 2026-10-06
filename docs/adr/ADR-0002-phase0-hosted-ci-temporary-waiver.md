# ADR-0002 — Phase 0 Hosted CI: Temporary Governance Waiver

**Status:** ACCEPTED — TEMPORARY WAIVER
**Type:** governance waiver / deviation (temporary, revocable)
**Proposed:** 2026-10-07 · **Accepted:** 2026-10-07 (human decision)
**Deciders:** product owner / architecture owners — approved 2026-10-07
**Related:** `docs/bootstrap/PHASE0_IMPLEMENTATION_MANDATE.md` §17, §21 (CI gates); roadmap v1.1 §2.2 (kickoff PR proves
the previous gate); `docs/bootstrap/ARCHITECTURE_TEST_CONTRACT.md` §26–27; `.github/workflows/ci.yml`.

---

## 1. Context — facts at the time of writing

| Item | State |
|---|---|
| Local Git repository | exists (`main`; commits `b407869 Phase 0 foundation`, `44ecd2a Accept ADR-0001 …`) |
| Local CI (`scripts/ci.ps1`) | **passing** — restore, format, Release build, unit, architecture, contract, EF pending-model check, Testcontainers integration, frontend install/format/lint/typecheck/admin build/b2b build |
| Full local validation | `dotnet build` 0 warnings/0 errors; `dotnet test` 161/161; frontend gates pass; Testcontainers PostgreSQL 17 + Redis 7 |
| Local deliberate architecture-failure proof | performed in Phase 0 (A003, A006, A016 failed as expected, then restored) |
| GitHub repository / remote | **does not exist** (no `origin`; `byrunoo/VelmorB2B` not found at last check) |
| GitHub-hosted workflow | **has never run** |
| Branch protection / ruleset, required `ci-gate` | **not configured** |
| Hosted architecture-failure and merge-blocking proof | **not performed** |

Therefore **Phase 0's original acceptance gate remains formally incomplete** (FAIL / INCOMPLETE for the CI
section of mandate §21: "Architecture-test failure blocks merge", "Contract/Integration tests run in CI",
"Frontend lint/typecheck/build run in CI").

## 2. Decision

**Accepted by human decision (2026-10-07).** Phase 0 hosted GitHub CI requirements are **NOT PASS** and remain
formally incomplete; local Phase 1 development may proceed by explicit human decision.

The user intentionally **defers** the external GitHub-hosted CI proof so that Phase 1 **local development** may
proceed. This record grants a temporary waiver of the roadmap rule that a kickoff PR must prove the previous
phase gate — **for the hosted-CI items listed in §3 only**. Every other Phase 0 gate has passed locally and
remains binding.

## 3. Deferred requirements

Status of each item: **DEFERRED — REQUIRED BEFORE COMMERCIAL RELEASE**

| # | Requirement | Done when |
|---|---|---|
| D1 | GitHub repository + `origin` remote | real repository URL exists and `git remote -v` shows it |
| D2 | `main` pushed | `git push -u origin main` succeeded |
| D3 | Hosted CI green | a GitHub Actions run of `ci.yml` on `main` succeeds (all jobs) |
| D4 | `ci-gate` required | `ci-gate` is a required status check for `main` |
| D5 | Branch protection / ruleset | `main` protected: pull request required, required checks strict/up-to-date |
| D6 | Deliberate hosted architecture-failure proof | a temporary branch with one controlled architecture violation makes the architecture step, its job and `ci-gate` fail on GitHub |
| D7 | Merge-blocking proof | a PR from that branch shows merge blocked by the failing required `ci-gate`; branch/PR closed, never merged |

## 4. What this waiver must NOT do

- It does not weaken, skip or remove any architecture test, security rule, secret scan or CI workflow job.
- It does not mark any unexecuted hosted check as PASS. Reports must keep stating "hosted CI not run".
- It does not remove the requirements permanently — D1–D7 remain mandatory.
- It does not change Phase 0 history: the Phase 0 report's final gate stays **FAIL / INCOMPLETE (hosted CI)**.
- It does not waive the database role separation required before the first Phase 1 tenant-scoped migration.

## 5. Scope and expiry

- Unblocks: Phase 1 kickoff and Phase 1 **local** development (local CI must stay green for every change).
- Does not unblock: any commercial/pilot/production release, release candidate or customer-facing deployment.
- Expires automatically when D1–D7 are completed (record the evidence and set this ADR to `SUPERSEDED` by the
  completion record), or when revoked.
- **Revocable at any time** by the product owner; the deferred items can be completed at any time, independent
  of the active phase.
- Every phase report while this waiver is active must contain a "Deferred Work" entry listing D1–D7.

## 6. Consequences

- Merge protection is currently procedural (local `scripts/ci.ps1` before every commit), not enforced by a
  server. Risk accepted temporarily by the product owner.
- The first push will run the full workflow; any hosted-only issue (case sensitivity, line endings, runner
  Docker) must be fixed without weakening rules.
