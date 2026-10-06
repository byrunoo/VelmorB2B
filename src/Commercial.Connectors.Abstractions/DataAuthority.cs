namespace Commercial.Connectors.Abstractions;

/// <summary>
/// Who is authoritative for a piece of data. Values mirror the roadmap vocabulary
/// (LOCAL / EXTERNAL / HYBRID). Policy evaluation is a Phase 2+ deliverable.
/// </summary>
public enum DataAuthority
{
    Local = 0,
    External = 1,
    Hybrid = 2,
}
