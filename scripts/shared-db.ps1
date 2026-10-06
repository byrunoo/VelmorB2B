# Shared Development PostgreSQL helper.
#
#   pwsh scripts/shared-db.ps1 inspect                 # read-only state report (login, privileges, schemas, tables, history)
#   pwsh scripts/shared-db.ps1 migrations              # list applied / pending EF migrations
#   pwsh scripts/shared-db.ps1 migrate                 # apply EF migrations (idempotent; never drops)
#   pwsh scripts/shared-db.ps1 api | worker            # run a host against the shared database
#   pwsh scripts/shared-db.ps1 cleanup <correlationId> # delete ONLY diagnostic rows with that correlation ID
#
# Host / port / database / user are set here for THIS process only. The password is NEVER in this
# repository: provide it once per machine via user-secrets (preferred)
#   dotnet user-secrets set "Commercial:Postgres:Password" "<password>" --id commercial-platform-dev
# or via the environment variable COMMERCIAL__POSTGRES__PASSWORD.
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('inspect', 'migrations', 'migrate', 'api', 'worker', 'cleanup')]
    [string]$Action,
    [Parameter(Position = 1)]
    [string]$CorrelationId
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$env:COMMERCIAL__POSTGRES__HOST = '192.168.1.250'
$env:COMMERCIAL__POSTGRES__PORT = '5432'
$env:COMMERCIAL__POSTGRES__DATABASE = 'commercial_platform_dev'
$env:COMMERCIAL__POSTGRES__USERNAME = 'commercial_platform_app'

switch ($Action) {
    'inspect' { dotnet run --file scripts/shared-db/SharedDb.cs -- inspect }
    'cleanup' { dotnet run --file scripts/shared-db/SharedDb.cs -- cleanup $CorrelationId }
    'migrations' {
        dotnet tool restore | Out-Null
        dotnet ef migrations list -p src/Commercial.Infrastructure -s src/Commercial.Infrastructure
    }
    'migrate' {
        dotnet tool restore | Out-Null
        dotnet ef database update -p src/Commercial.Infrastructure -s src/Commercial.Infrastructure
    }
    'api' { dotnet run --project src/Commercial.Api --launch-profile 'Commercial.Api (shared-db)' }
    'worker' { dotnet run --project src/Commercial.Worker --launch-profile 'Commercial.Worker (shared-db)' }
}
exit $LASTEXITCODE
