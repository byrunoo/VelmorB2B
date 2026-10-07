# Shared Development PostgreSQL helper (ADR-0001 three-role model).
#
#   pwsh scripts/shared-db.ps1 inspect [runtime|migration|dba]  # read-only state, ownership and privilege report
#   pwsh scripts/shared-db.ps1 verify-privileges [runtime|migration]  # real privilege probes, all rolled back
#   pwsh scripts/shared-db.ps1 migrations                        # list applied / pending EF migrations (owner identity)
#   pwsh scripts/shared-db.ps1 migrate                           # apply EF migrations with dotnet ef (owner identity)
#   pwsh scripts/shared-db.ps1 provision                         # EF migrations + Hangfire schema (owner identity)
#   pwsh scripts/shared-db.ps1 role-split                        # DBA: run scripts/db/shared-dev-role-split.sql
#   pwsh scripts/shared-db.ps1 set-role-passwords                # DBA: set owner/maintenance passwords (SCRAM verifiers)
#   pwsh scripts/shared-db.ps1 api | worker                      # run a host as the RUNTIME identity
#   pwsh scripts/shared-db.ps1 cleanup <correlationId>           # delete ONLY diagnostic rows with that correlation ID
#
# Host / port / database / usernames are set here for THIS process only. Passwords are NEVER in this repository:
#   runtime  (API/Worker)   dotnet user-secrets set "Commercial:Postgres:Runtime:Password"   "<pw>" --id commercial-platform-dev
#   owner    (migrations)   dotnet user-secrets set "Commercial:Postgres:Migration:Password" "<pw>" --id commercial-platform-ops
#   maintenance (ops only)  dotnet user-secrets set "Commercial:Postgres:Maintenance:Password" "<pw>" --id commercial-platform-ops
#   DBA (role split only)   dotnet user-secrets set "Commercial:Postgres:Dba:Username" "<superuser>" --id commercial-platform-ops
#                           dotnet user-secrets set "Commercial:Postgres:Dba:Password" "<pw>"        --id commercial-platform-ops
# The ops store is never loaded by the API/Worker runtime. Environment variables (COMMERCIAL__POSTGRES__…) also work.
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('inspect', 'verify-privileges', 'migrations', 'migrate', 'provision', 'role-split', 'set-role-passwords', 'api', 'worker', 'cleanup')]
    [string]$Action,
    [Parameter(Position = 1)]
    [string]$Argument
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$env:COMMERCIAL__POSTGRES__HOST = '192.168.1.250'
$env:COMMERCIAL__POSTGRES__PORT = '5432'
$env:COMMERCIAL__POSTGRES__DATABASE = 'commercial_platform_dev'
$env:COMMERCIAL__POSTGRES__RUNTIME__USERNAME = 'commercial_platform_app'

function Use-MigrationIdentity {
    $env:COMMERCIAL__POSTGRES__MIGRATION__USERNAME = 'commercial_platform_owner'
}

switch ($Action) {
    'inspect' {
        $identity = if ($Argument) { $Argument } else { 'runtime' }
        if ($identity -eq 'migration') { Use-MigrationIdentity }
        dotnet run --file scripts/shared-db/SharedDb.cs -- inspect --as $identity
    }
    'verify-privileges' {
        $identity = if ($Argument) { $Argument } else { 'runtime' }
        if ($identity -eq 'migration') { Use-MigrationIdentity }
        dotnet run --file scripts/shared-db/SharedDb.cs -- verify-privileges --as $identity
    }
    'role-split' { dotnet run --file scripts/shared-db/SharedDb.cs -- role-split }
    'set-role-passwords' { dotnet run --file scripts/shared-db/SharedDb.cs -- set-role-passwords }
    'cleanup' { dotnet run --file scripts/shared-db/SharedDb.cs -- cleanup $Argument }
    'migrations' {
        Use-MigrationIdentity
        dotnet tool restore | Out-Null
        dotnet ef migrations list -p src/Commercial.Infrastructure -s src/Commercial.Infrastructure
    }
    'migrate' {
        Use-MigrationIdentity
        dotnet tool restore | Out-Null
        dotnet ef database update -p src/Commercial.Infrastructure -s src/Commercial.Infrastructure
    }
    'provision' {
        Use-MigrationIdentity
        dotnet run --project src/Commercial.Worker --launch-profile 'Commercial.Worker (shared-db)' -- --provision-database
    }
    'api' { dotnet run --project src/Commercial.Api --launch-profile 'Commercial.Api (shared-db)' }
    'worker' { dotnet run --project src/Commercial.Worker --launch-profile 'Commercial.Worker (shared-db)' }
}
exit $LASTEXITCODE
