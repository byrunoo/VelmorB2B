# Runs the same gates as .github/workflows/ci.yml locally (except gitleaks, which needs git history).
# Usage: pwsh scripts/ci.ps1 [-SkipIntegration] [-SkipFrontend]
param(
    [switch]$SkipIntegration,
    [switch]$SkipFrontend
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Step([string]$name, [scriptblock]$action) {
    Write-Host "`n=== $name ===" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host "PASSED: $name" -ForegroundColor Green
}

Step 'restore' { dotnet tool restore; dotnet restore Commercial.slnx }
Step 'format validation' { dotnet format Commercial.slnx --verify-no-changes --no-restore }
Step 'build (warnings as errors)' { dotnet build Commercial.slnx --no-restore -c Release }
Step 'unit tests' { dotnet test --project tests/Commercial.UnitTests --no-build -c Release }
Step 'architecture tests' { dotnet test --project tests/Commercial.ArchitectureTests --no-build -c Release }
Step 'contract tests' { dotnet test --project tests/Commercial.ContractTests --no-build -c Release }
Step 'pending model changes' { dotnet ef migrations has-pending-model-changes -p src/Commercial.Infrastructure -s src/Commercial.Infrastructure --no-build --configuration Release }

if (-not $SkipIntegration) {
    Step 'integration tests (Testcontainers; requires Docker)' { dotnet test --project tests/Commercial.IntegrationTests --no-build -c Release }
}

if (-not $SkipFrontend) {
    Push-Location frontend
    try {
        Step 'frontend install' { npm ci }
        Step 'frontend format check' { npm run format:check }
        Step 'frontend lint' { npm run lint }
        Step 'frontend typecheck' { npm run typecheck }
        Step 'frontend build admin' { npm run build -w @commercial/admin }
        Step 'frontend build b2b' { npm run build -w @commercial/b2b }
    }
    finally {
        Pop-Location
    }
}

Write-Host "`nALL CI GATES PASSED" -ForegroundColor Green
