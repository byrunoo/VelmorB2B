# Bootstrap package (verbatim)

The documents in this folder are the original Phase 0 bootstrap package, kept **unchanged**:

| File | Role |
|---|---|
| `PHASE0_IMPLEMENTATION_MANDATE.md` | Active phase mandate (referenced by `docs/architecture/active-phase.json`) |
| `ARCHITECTURE_TEST_CONTRACT.md` | Architecture test contract A001–A018 |
| `COMMERCIAL_PLATFORM_ROADMAP_v1.1.md` | Roadmap (phases, KAPI-1, decision register) |
| `CLAUDE_CODE_START_HERE.md` | Agent start instructions |
| `ArchitectureRules.cs` | Starting sample; superseded by `tests/Commercial.ArchitectureTests` (not compiled) |

They were moved here from `docs/` to keep the root clean. Paths they mention resolve as follows:
`docs/architecture/*`, `docs/adr/*` and root `CLAUDE.md` now exist; `PHASE0_IMPLEMENTATION_MANDATE.md` and
`ARCHITECTURE_TEST_CONTRACT.md` live in this folder (the mandate's §5 root placement is recorded as a
deviation in the Phase 0 report). The consolidated, normative architecture is
`docs/architecture/frozen-architecture-v1.0.md`.

`reference/` holds the BusinessOS/Kumpara source archive used read-only for Public API fit-gap and UI
technology analysis. It is **gitignored**, may contain secrets, and must never be copied into this codebase.
