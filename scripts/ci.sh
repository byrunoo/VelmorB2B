#!/usr/bin/env bash
# Runs the same gates as .github/workflows/ci.yml locally (except gitleaks, which needs git history).
# Usage: bash scripts/ci.sh [--skip-integration] [--skip-frontend]
set -euo pipefail
cd "$(dirname "$0")/.."

SKIP_INTEGRATION=0
SKIP_FRONTEND=0
for arg in "$@"; do
  case "$arg" in
    --skip-integration) SKIP_INTEGRATION=1 ;;
    --skip-frontend) SKIP_FRONTEND=1 ;;
  esac
done

step() { echo; echo "=== $1 ==="; shift; "$@"; echo "PASSED"; }

step "restore" bash -c "dotnet tool restore && dotnet restore Commercial.slnx"
step "format validation" dotnet format Commercial.slnx --verify-no-changes --no-restore
step "build (warnings as errors)" dotnet build Commercial.slnx --no-restore -c Release
step "unit tests" dotnet test --project tests/Commercial.UnitTests --no-build -c Release
step "architecture tests" dotnet test --project tests/Commercial.ArchitectureTests --no-build -c Release
step "contract tests" dotnet test --project tests/Commercial.ContractTests --no-build -c Release
step "pending model changes" dotnet ef migrations has-pending-model-changes -p src/Commercial.Infrastructure -s src/Commercial.Infrastructure --no-build --configuration Release

if [ "$SKIP_INTEGRATION" -eq 0 ]; then
  step "integration tests (Testcontainers; requires Docker)" dotnet test --project tests/Commercial.IntegrationTests --no-build -c Release
fi

if [ "$SKIP_FRONTEND" -eq 0 ]; then
  (
    cd frontend
    step "frontend install" npm ci
    step "frontend format check" npm run format:check
    step "frontend lint" npm run lint
    step "frontend typecheck" npm run typecheck
    step "frontend build admin" npm run build -w @commercial/admin
    step "frontend build b2b" npm run build -w @commercial/b2b
  )
fi

echo
echo "ALL CI GATES PASSED"
