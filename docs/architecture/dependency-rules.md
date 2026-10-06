# Dependency Rules (executable)

Every rule below is enforced by `tests/Commercial.ArchitectureTests` (fast, deterministic, no network)
and/or frontend ESLint. A failing rule fails `dotnet test` and the CI `architecture` job, which blocks merge.
Failure messages start with the contract's failure code and list offending types/projects/files.

| Rule | What is checked | Where | Failure code |
|---|---|---|---|
| A001 | Domain has no dependency on EF Core, ASP.NET Core, DI, Hangfire, Redis, Npgsql, Serilog, OTel, HTTP, Application/Infrastructure/Api/Worker/Connectors (types + assembly refs) | `LayerDependencyRules` | `DOMAIN_INFRASTRUCTURE_DEPENDENCY_VIOLATION` |
| A002 | Application has no dependency on Infrastructure/Api/Worker, EF/ASP.NET/Hangfire/Redis/Npgsql/Serilog, or any `Commercial.Connectors.*` other than Abstractions (convention, not a static list) | `LayerDependencyRules` | `APPLICATION_IMPLEMENTATION_DEPENDENCY_VIOLATION` |
| A003 | Domain + Application: (1) no provider assembly/namespace dependency, (2) no type/namespace declaration containing an exact provider token, (3) no provider token in source identifiers/string literals (provider branching) | `ProviderLeakageRules` | `PROVIDER_LEAKAGE_IN_CORE` |
| A004 | No production assembly / csproj references `BusinessOS.*` or `Kumpara.*` (assembly refs, namespaces, project/package/assembly references) | `ExternalSystemDependencyRules` | `KUMPARA_INTERNAL_DEPENDENCY_VIOLATION` |
| A005 | No production reference to legacy Sales Ops roots (`Data/legacy-sales-ops-identifiers.json`) via assembly/project/package refs, namespace deps or declarations | `ExternalSystemDependencyRules` | `LEGACY_SALES_OPS_DEPENDENCY_VIOLATION` |
| A006 | Api types do not depend on EF Core, Npgsql, Redis, Hangfire or `Commercial.Infrastructure.{Persistence,Messaging,Caching,BackgroundJobs}` — endpoints go through `IRequestDispatcher` | `LayerDependencyRules` | `API_BYPASS_APPLICATION_BOUNDARY` |
| A007 | Worker types do not depend on EF Core, Npgsql, Redis, `Commercial.Infrastructure.Persistence` or Domain, and implement no use-case/outbox handler contracts | `LayerDependencyRules` | `WORKER_BUSINESS_LOGIC_BOUNDARY_VIOLATION` |
| A008 | Connectors.Abstractions: no provider dependency, no `Commercial.*` references, no provider token in declarations or source | `ProviderLeakageRules` | `CONNECTOR_ABSTRACTION_PROVIDER_LEAKAGE` |
| A009 | `Commercial.Connectors.<X>` projects never reference another connector implementation (rule + synthetic self-test) | `ProjectGraphRules` | `CONNECTOR_CROSS_DEPENDENCY_VIOLATION` |
| A010 | Infrastructure implements no command/query handler, validator or outbox handler; no `*Policy/*Rule/*Service/*Engine/*Handler/...` types under `Commercial.Infrastructure.Persistence` | `LayerDependencyRules` | `INFRASTRUCTURE_BUSINESS_RULE_LEAKAGE` |
| A011 | csproj project-reference graph (src + tests) is acyclic (rule + synthetic self-test) | `ProjectGraphRules` | `CIRCULAR_PROJECT_DEPENDENCY` |
| A012 | `src/` projects reference no test project/package; production assemblies reference no `*Tests`/xunit assembly | `ProjectGraphRules` | `PRODUCTION_TEST_DEPENDENCY_VIOLATION` |
| A013 | Outbox row fields; claim SQL uses `FOR UPDATE SKIP LOCKED` + lease stamp + expired-lease reclaim; completion/failure guarded by lease; integration proofs tagged `[Trait("A013", ...)]` exist (multi-worker-claim, lease-expiry-recovery, duplicate-delivery-idempotency, hangfire-dispatch) | `OutboxOwnershipRules` + `tests/Commercial.IntegrationTests/Outbox` | `OUTBOX_OWNERSHIP_OR_IDEMPOTENCY_VIOLATION` |
| A014 | Always: scope seam + initializer hook wired; **no EF global query filters**; `No Reset On Close` never enabled; RLS DDL requires ADR-0001 ACCEPTED; phase ≥ 1 requires ADR-0001 ACCEPTED. Level A (implementation-triggered, any phase): scoped entity ⇒ NOT NULL scope columns + ENABLE/FORCE RLS + USING/WITH CHECK policy; isolation implementation ⇒ non-no-op initializer + core integration proofs. Level B (full Phase 1 suite) = acceptance run, not a test flag | `TenantIsolationReadinessRules` | `TENANT_ISOLATION_DECISION_GATE_VIOLATION` |
| A015 | Narrow secret scan (connection-string passwords, secret-like JSON settings, private keys, cloud tokens) over src/tests/frontend/scripts/.github/root config; CI also runs gitleaks | `SecretAndFrontendRules` + `.github/workflows/ci.yml` | `HARDCODED_SECRET_DETECTED` |
| A016 | `frontend/admin` ↔ `frontend/b2b` never import each other; `frontend/shared` imports neither | ESLint (`frontend/eslint.config.js`) + `SecretAndFrontendRules` | `FRONTEND_CROSS_APP_IMPORT` |
| A017 | No provider tokens and no absolute URLs in frontend application source; HTTP only through the shared Commercial API client | ESLint + `SecretAndFrontendRules` | `FRONTEND_EXTERNAL_PROVIDER_BYPASS` |
| A018 | Reads `docs/architecture/active-phase.json`; applies that phase's data from `tests/Commercial.ArchitectureTests/PhaseScope/phase-scope.json` (forbidden projects, business type names, module namespaces, frontend screens); fails if the active phase has no scope data | `ActivePhaseScopeRules` | `ACTIVE_PHASE_SCOPE_VIOLATION` |
| Contract boundary | While the active phase forbids external contract tests, `tests/Commercial.ContractTests` contains no provider token | `ActivePhaseScopeRules` | `PHASE0_EXTERNAL_CONTRACT_SCOPE_VIOLATION` |

## Provider-token policy

`tests/Commercial.ArchitectureTests/Support/ProviderTokens.cs`. Identifiers are split into words
(PascalCase/camelCase/acronym boundaries, `.`, `_`, `-`) and a token matches only as a contiguous,
case-insensitive word sequence. Tokens: `Kumpara, BusinessOS, LogoTiger, LogoGo, LogoIsbasi, LogoErp,
MikroErp, Netsis, Parasut, Nebim, NebimV3, WinnerErp, SapBusinessOne`. Plain `Logo`, `SAP` and generic
English words (`Winner`) are deliberately not tokens. Scanner self-tests assert `CompanyLogo`, `LogoUrl`,
`BrandLogo`, `SaplingConfiguration` (and more) produce no match.

Adding a connector: add its exact root token here **and** in `ProviderTokens.All`.

## Exceptions

None. Global ignore lists are forbidden. A narrow exception must be documented in the test with a
reason and an issue/ADR reference.
