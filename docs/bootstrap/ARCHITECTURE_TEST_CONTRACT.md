# Commercial Platform — Architecture Test Contract

**Status:** FROZEN FOR ACTIVE PHASE  
**Package revision:** 3  
**Applies to:** `tests/Commercial.ArchitectureTests`  
**Active phase source:** `docs/architecture/active-phase.json`  
**CI behavior:** Required rule failure = build/merge failure

---

## 1. Purpose

Architecture tests are executable guardrails, not documentation.

They exist to catch boundary violations before code review and to keep coding agents from silently widening the architecture.

---

## 2. Tooling

Recommended Phase 0 baseline:

- xUnit
- NetArchTest.Rules or equivalent
- custom reflection/project-reference tests where appropriate
- targeted source/Roslyn checks only where assembly rules cannot express the invariant

Rules must be deterministic and fast.

---

## 3. Production assemblies

Minimum Phase 0 production assemblies:

```text
Commercial.Domain
Commercial.Application
Commercial.Infrastructure
Commercial.Api
Commercial.Worker
Commercial.Connectors.Abstractions
```

Test assemblies are not part of the production dependency graph.

---

## 4. Provider-token matching policy

Do not use generic substring bans that collide with legitimate product vocabulary.

In particular, the plain token/string `Logo` must **not** be globally forbidden because legitimate code may contain:

- `CompanyLogo`
- `LogoUrl`
- `BrandLogo`

Likewise do not use a broad `SAP` substring ban.

Provider leakage checks should use exact known provider namespace/type tokens or exact lexical tokens such as:

```text
Kumpara
BusinessOS
LogoTiger
LogoGo
LogoIsbasi
LogoErp
MikroErp
Netsis
Parasut
Nebim
SapBusinessOne
```

When new connector implementations are introduced, add their exact root namespace/assembly token.

---

## 5. A001 — Domain independence

`Commercial.Domain` must not depend on infrastructure/runtime implementation frameworks.

Forbidden dependency roots include:

```text
Microsoft.EntityFrameworkCore
Microsoft.AspNetCore
Hangfire
StackExchange.Redis
Npgsql
Serilog
OpenTelemetry
Commercial.Infrastructure
Commercial.Api
Commercial.Worker
Commercial.Connectors.* implementation assemblies
```

`Commercial.Connectors.Abstractions` is also not a normal Domain dependency; connector boundaries belong outside the core domain.

**Failure:** `DOMAIN_INFRASTRUCTURE_DEPENDENCY_VIOLATION`

---

## 6. A002 — Application independence

`Commercial.Application` may depend on Domain and application-safe abstractions but may not depend on implementation assemblies.

Forbidden:

```text
Commercial.Infrastructure
Commercial.Api
Commercial.Worker
Commercial.Connectors.Kumpara
Commercial.Connectors.LogoTiger
Commercial.Connectors.LogoGo
Commercial.Connectors.LogoIsbasi
Commercial.Connectors.LogoErp
Commercial.Connectors.MikroErp
Commercial.Connectors.Netsis
Commercial.Connectors.Parasut
Commercial.Connectors.Nebim
Commercial.Connectors.SapBusinessOne
```

Provider implementations added later must be included by convention/root namespace rather than relying on a forever-static list.

**Failure:** `APPLICATION_IMPLEMENTATION_DEPENDENCY_VIOLATION`

---

## 7. A003 — Provider leakage in core

Core scope:

```text
Commercial.Domain
Commercial.Application
```

Core may not reference provider-specific assemblies/namespaces/types or branch on provider identity.

Checks must use the exact-token policy from section 4.

Examples that must fail in core:

```text
BusinessOSClient
KumparaOrderDto
LogoTigerOrderService
LogoErpCapabilitySwitch
SapBusinessOneAdapter
```

Examples that must **not** fail merely by name:

```text
CompanyLogo
LogoUrl
BrandLogo
SaplingConfiguration
```

A003 must perform **both** of these checks:

1. dependency/reference scanning for provider-specific assemblies/namespaces, and
2. declaration scanning over every type defined in `Commercial.Domain` and `Commercial.Application`.

Declaration scanning must inspect each defined type's:

- `Name`, and
- `Namespace` / `FullName`

against the exact provider tokens from section 4. This is required so a core-defined type such as `KumparaPriceMapper` or `LogoTigerOrderPolicy` fails even when it has no provider assembly dependency.

Provider-name branching in method bodies/string constants may additionally require Roslyn/source analysis where dependency/declaration scanning cannot detect it.

The scanner itself must be tested against legitimate branding names. At minimum `CompanyLogo`, `LogoUrl`, `BrandLogo` and `SaplingConfiguration` must return **no provider-token match**.

**Failure:** `PROVIDER_LEAKAGE_IN_CORE`

---

## 8. A004 — Kumpara internal dependency forbidden

No Commercial Platform production assembly may reference Kumpara/BusinessOS internal implementation assemblies.

Forbidden examples:

```text
BusinessOS.Domain
BusinessOS.Application
BusinessOS.Infrastructure
BusinessOS.Persistence
```

When a future `Commercial.Connectors.Kumpara` project exists, it still communicates through HTTP/Public API DTOs and may not reference these internal assemblies.

**Failure:** `KUMPARA_INTERNAL_DEPENDENCY_VIOLATION`

---

## 9. A005 — Legacy Sales Ops dependency forbidden

No production assembly may reference known legacy Sales Ops assemblies/packages/root namespaces.

The rule should inspect:

- project references,
- assembly references,
- package references,
- known root namespaces.

Known legacy identifiers are updated when the legacy repository inventory is available.

**Failure:** `LEGACY_SALES_OPS_DEPENDENCY_VIOLATION`

---

## 10. A006 — API may not bypass Application boundary

`Commercial.Api` is the HTTP composition/adaptation layer.

Endpoint/controller types may not directly depend on:

```text
CommercialDbContext
Microsoft.EntityFrameworkCore.DbContext
NpgsqlConnection
Redis implementation clients
Hangfire storage implementation
```

Business mutations must flow through Application use cases/ports.

**Failure:** `API_BYPASS_APPLICATION_BOUNDARY`

---

## 11. A007 — Worker contains no commercial business logic

Worker jobs orchestrate background execution.

They may call application commands/use cases or infrastructure dispatchers, but may not directly mutate business aggregates through DbContext.

Outbox/infrastructure persistence is an allowed infrastructure concern when kept under an explicit infrastructure namespace.

**Failure:** `WORKER_BUSINESS_LOGIC_BOUNDARY_VIOLATION`

---

## 12. A008 — Connector abstractions are provider-independent

`Commercial.Connectors.Abstractions` may not contain provider-specific types/namespaces.

Use exact provider tokens from section 4.

Do **not** use patterns such as:

```text
*Logo*
*SAP*
```

because they produce false positives.

Valid generic abstractions include capability/result/error/identity primitives that do not encode a provider.

A008 must scan both dependencies **and declarations** in `Commercial.Connectors.Abstractions`. Type names and namespaces containing one of the exact provider tokens from section 4 must fail even if they do not reference a provider implementation assembly.

**Failure:** `CONNECTOR_ABSTRACTION_PROVIDER_LEAKAGE`

---

## 13. A009 — Connector implementations cannot depend on each other

Future connectors such as:

```text
Commercial.Connectors.Kumpara
Commercial.Connectors.LogoTiger
Commercial.Connectors.MikroErp
```

may not reference one another.

They may depend on provider-independent abstractions and their own transport dependencies.

**Failure:** `CONNECTOR_CROSS_DEPENDENCY_VIOLATION`

---

## 14. A010 — Infrastructure must not become a business-rule layer

Infrastructure may reference Domain/Application where technically required for persistence mappings or implementation contracts.

However, commercial decisions/rules must not be implemented as hidden Infrastructure behavior.

Executable minimum:

- no Application use-case namespace implemented inside persistence mapping classes,
- no endpoint-like business service exposed only from Infrastructure,
- known domain policy/service naming conventions cannot live under persistence namespaces.

Prefer dependency rules plus reviewable naming conventions over brittle text scans.

**Failure:** `INFRASTRUCTURE_BUSINESS_RULE_LEAKAGE`

---

## 15. A011 — Circular production project dependencies forbidden

Production project references must form an acyclic graph.

At minimum, build a project-reference graph and fail on cycles.

**Failure:** `CIRCULAR_PROJECT_DEPENDENCY`

---

## 16. A012 — Production assemblies cannot reference test assemblies

No project under `src/` may reference:

```text
*.Tests
Commercial.UnitTests
Commercial.IntegrationTests
Commercial.ArchitectureTests
Commercial.ContractTests
```

**Failure:** `PRODUCTION_TEST_DEPENDENCY_VIOLATION`

---

## 17. A013 — Outbox multi-worker ownership contract

The Phase 0 outbox dispatcher must be safe with multiple Worker instances.

Architecture/integration tests must verify the implemented claim model includes:

- a concurrency-safe database claim,
- ownership/lease state,
- lease expiry/recovery,
- no simultaneous active ownership of one row.

Frozen baseline implementation uses:

- PostgreSQL `FOR UPDATE SKIP LOCKED`, and
- persisted `LockedBy` / `LockedUntilUtc` lease fields.

If an equivalent implementation is substituted, it must prove the same semantics.

Also require a test handler that is idempotent under duplicate delivery.

**Failure:** `OUTBOX_OWNERSHIP_OR_IDEMPOTENCY_VIOLATION`

---

## 18. A014 — Tenant isolation strategy must remain pluggable in Phase 0

When active phase is 0:

- real RLS policies are not required,
- real global tenant query filters are not required,
- a centralized execution-scope abstraction must exist,
- DbContext/connection setup must expose a database-session initializer hook,
- the default Phase 0 initializer is no-op,
- integration tests must prove the hook runs through the real connection path.

Before phase 1 activation, `ADR-0001-tenant-isolation-decision-gate.md` must no longer be undecided.

A phase-1 kickoff guard should fail if the ADR status remains `REQUIRED BEFORE PHASE 1 — NOT YET DECIDED`.

**Failure:** `TENANT_ISOLATION_DECISION_GATE_VIOLATION`

---

## 19. A015 — Hard-coded secrets forbidden

Repository code/config must not contain committed production secrets.

At minimum CI should scan for obvious hard-coded:

- passwords,
- API keys,
- client secrets,
- real connection strings.

Architecture tests may add narrow checks, but a dedicated secret-scanning tool is preferred.

**Failure:** `HARDCODED_SECRET_DETECTED`

---

## 20. A016 — Frontend app boundaries

`frontend/admin` and `frontend/b2b` may not import each other's internal source trees directly.

Shared code must flow through:

```text
frontend/shared
```

or an explicit shared workspace package.

Enforce with ESLint/import-boundary rules in CI.

**Failure:** `FRONTEND_CROSS_APP_IMPORT`

---

## 21. A017 — Frontend provider bypass forbidden

Browser applications call Commercial API only.

Do not hard-code or directly call Kumpara/Logo/ERP endpoints from admin or B2B frontend.

Configuration values for the Commercial API origin are allowed.

**Failure:** `FRONTEND_EXTERNAL_PROVIDER_BYPASS`

---

## 22. A018 — Active-phase scope guard

A018 is **phase-aware**.

It must read:

`docs/architecture/active-phase.json`

It must not permanently encode "Phase 0 forever".

### When phase = 0

Forbidden implementation includes known Phase 1+ business modules/types/projects such as:

```text
Quote aggregate/module
SalesOrder aggregate/module
PricingEngine
CreditPolicy
CommercialAccount business module
InvoiceReference business module
RentalContract
Commercial.Connectors.Kumpara implementation
Commercial.Connectors.LogoTiger implementation
```

The guard should use explicit project/module/type conventions, not broad keyword matching that catches harmless documentation or primitives.

### Later phases

The guard changes according to the active phase mandate. For example, once Quote is an approved active-phase deliverable, Quote is no longer a forbidden implementation merely because Phase 0 once banned it.

### Kickoff PR exemption

A human-approved phase kickoff PR may change:

- `docs/architecture/active-phase.json`,
- the next phase mandate,
- A018 phase-specific allow/deny data/tests,

without creating an ADR solely for the phase-scope transition.

This exemption does not permit weakening the Frozen Architecture itself.

**Failure:** `ACTIVE_PHASE_SCOPE_VIOLATION`

---

## 23. Phase 0 contract-test boundary

The existence of `Commercial.ContractTests` is required in Phase 0, but it must not fabricate external provider contracts.

Required Phase 0 contract tests:

1. Commercial API OpenAPI document snapshot/compatibility test.
2. Standard error-envelope schema/semantic tests.
3. Optional health/version endpoint contract test.

External connector contract tests begin only in the relevant integration phase.

If a Phase 0 test fixture invents Kumpara/Logo request/response contracts, that is a scope violation.

**Failure:** `PHASE0_EXTERNAL_CONTRACT_SCOPE_VIOLATION`

---

## 24. Architecture test implementation principles

Rules must:

- be deterministic,
- require no external network,
- run quickly,
- show offending types/projects in failure output,
- avoid ever-growing global ignore lists.

If a narrow exception is necessary:

1. document why,
2. reference issue/ADR where relevant,
3. keep the exception as narrow as possible.

Global ignore is forbidden.

---

## 25. CI order

Recommended fast-fail order:

```text
Restore
Build
Unit Tests
Architecture Tests
Contract Tests
Integration Tests
Frontend Lint/Typecheck/Build
```

---

## 26. Merge blockers

The following are mandatory merge blockers in Phase 0:

```text
A001 Domain independence
A002 Application independence
A003 Provider leakage
A004 Kumpara internal dependency
A005 Legacy Sales Ops dependency
A006 API boundary
A011 Circular dependency
A012 Test isolation
A013 Outbox ownership/idempotency
A014 Tenant-isolation readiness gate
A016 Frontend cross-app imports
A017 Frontend provider bypass
A018 Active-phase scope guard
Phase 0 contract-test boundary
```

Other rules should also be executable where practical and may be promoted to blocker during Phase 0.

---

## 27. Deliberate failure proof

Before Phase 0 is accepted, at least one required architecture rule must be intentionally violated in a temporary/local test change and shown to fail, then restored.

The final repository must contain only the passing state; the implementation report records which rule was proven.

---

## 28. Contract change policy

Weakening a Frozen Architecture invariant requires:

1. architectural reason,
2. rejected alternatives,
3. impact analysis,
4. ADR and/or Frozen Architecture update.

### Explicit exception: normal phase activation

Changing A018's phase-specific scope as part of a dedicated, human-approved kickoff PR is not considered architecture weakening when:

- `active-phase.json` advances to an already-approved roadmap phase,
- the relevant phase mandate is added/activated,
- Frozen Architecture invariants remain unchanged.

Do not use this exception to bypass architecture rules for convenience.
