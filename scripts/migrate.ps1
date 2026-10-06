# Applies EF Core migrations to the database in $env:ConnectionStrings__Commercial
# (defaults to the local docker-compose database). Production: run from the release pipeline
# with a migration-capable role; never commit credentials.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $PSScriptRoot)
dotnet tool restore
dotnet ef database update -p src/Commercial.Infrastructure -s src/Commercial.Infrastructure
exit $LASTEXITCODE
