#!/usr/bin/env bash

set -u
set -o pipefail

ROOT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
REPORT_DIR="$ROOT_DIR/artifacts/dead-code"
RUN_BACKEND=1
RUN_FRONTEND=1

usage() {
  echo "Usage: bash ./scripts/dead-code.sh [--backend-only] [--frontend-only] [--report-dir <path>]"
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --backend-only)
      RUN_FRONTEND=0
      ;;
    --frontend-only)
      RUN_BACKEND=0
      ;;
    --report-dir)
      if [[ $# -lt 2 ]]; then
        usage
        exit 1
      fi

      REPORT_DIR="$2"
      shift
      ;;
    -h|--help)
      usage
      exit 0
      ;;
    *)
      echo "Unknown argument: $1" >&2
      usage
      exit 1
      ;;
  esac

  shift
done

mkdir -p "$REPORT_DIR"

TIMESTAMP=$(date -u +"%Y-%m-%dT%H:%M:%SZ")
SUMMARY_FILE="$REPORT_DIR/summary.md"

BACKEND_LOG="$REPORT_DIR/backend-build.log"
BACKEND_FINDINGS="$REPORT_DIR/backend-dead-code-findings.txt"
BACKEND_STATUS="skipped"
BACKEND_EXIT=0

FRONTEND_TYPECHECK_LOG="$REPORT_DIR/frontend-typecheck.log"
FRONTEND_LINT_LOG="$REPORT_DIR/frontend-lint.log"
FRONTEND_REACHABILITY_LOG="$REPORT_DIR/frontend-reachability.log"
FRONTEND_UNREACHABLE="$REPORT_DIR/frontend-unreachable-files.txt"
FRONTEND_UNREACHABLE_APP="$REPORT_DIR/frontend-unreachable-app-files.txt"
FRONTEND_UNREACHABLE_TEST="$REPORT_DIR/frontend-unreachable-test-files.txt"
FRONTEND_UNUSED_EXPORTS="$REPORT_DIR/frontend-unused-exports.txt"
FRONTEND_UNUSED_LINT="$REPORT_DIR/frontend-unused-lint-findings.txt"
FRONTEND_STATUS="skipped"
FRONTEND_TYPECHECK_EXIT=0
FRONTEND_LINT_EXIT=0
FRONTEND_REACHABILITY_EXIT=0

BACKEND_WARNING_PATTERN='warning (CA1811|CA1823|CS0162|CS0168|CS0219|CS0414|IDE0005|IDE0051|IDE0060)\b'
FRONTEND_UNUSED_PATTERN='@typescript-eslint/no-unused-vars|no-unused-vars'

: >"$BACKEND_LOG"
: >"$BACKEND_FINDINGS"
: >"$FRONTEND_TYPECHECK_LOG"
: >"$FRONTEND_LINT_LOG"
: >"$FRONTEND_REACHABILITY_LOG"
: >"$FRONTEND_UNREACHABLE"
: >"$FRONTEND_UNREACHABLE_APP"
: >"$FRONTEND_UNREACHABLE_TEST"
: >"$FRONTEND_UNUSED_EXPORTS"
: >"$FRONTEND_UNUSED_LINT"

count_lines() {
  local file_path="$1"

  if [[ -f "$file_path" ]]; then
    awk 'END { print NR }' "$file_path"
  else
    echo 0
  fi
}

if [[ "$RUN_BACKEND" -eq 1 ]]; then
  pushd "$ROOT_DIR" >/dev/null || exit 1

  set +e
  dotnet build src/Kautuka.slnx \
    -nologo \
    -p:TreatWarningsAsErrors=false \
    >"$BACKEND_LOG" 2>&1
  BACKEND_EXIT=$?
  set -e

  if grep -E "$BACKEND_WARNING_PATTERN" "$BACKEND_LOG" >"$BACKEND_FINDINGS"; then
    BACKEND_STATUS="findings"
  else
    : >"$BACKEND_FINDINGS"
    if [[ "$BACKEND_EXIT" -eq 0 ]]; then
      BACKEND_STATUS="clean"
    else
      BACKEND_STATUS="build-error"
    fi
  fi

  popd >/dev/null || exit 1
fi

if [[ "$RUN_FRONTEND" -eq 1 ]]; then
  pushd "$ROOT_DIR/src/frontend/web" >/dev/null || exit 1

  set +e
  npm run type-check >"$FRONTEND_TYPECHECK_LOG" 2>&1
  FRONTEND_TYPECHECK_EXIT=$?

  npm run lint >"$FRONTEND_LINT_LOG" 2>&1
  FRONTEND_LINT_EXIT=$?

  npm run dead:files >"$FRONTEND_REACHABILITY_LOG" 2>&1
  FRONTEND_REACHABILITY_EXIT=$?
  set -e

  if [[ -f dead-files-runtime.txt ]]; then
    cp dead-files-runtime.txt "$FRONTEND_UNREACHABLE"
  else
    : >"$FRONTEND_UNREACHABLE"
  fi

  if [[ -f dead-files-app-runtime.txt ]]; then
    cp dead-files-app-runtime.txt "$FRONTEND_UNREACHABLE_APP"
  else
    : >"$FRONTEND_UNREACHABLE_APP"
  fi

  if [[ -f dead-files-tests-runtime.txt ]]; then
    cp dead-files-tests-runtime.txt "$FRONTEND_UNREACHABLE_TEST"
  else
    : >"$FRONTEND_UNREACHABLE_TEST"
  fi

  if [[ -f dead-exports-runtime.txt ]]; then
    cp dead-exports-runtime.txt "$FRONTEND_UNUSED_EXPORTS"
  else
    : >"$FRONTEND_UNUSED_EXPORTS"
  fi

  if grep -E "$FRONTEND_UNUSED_PATTERN" "$FRONTEND_LINT_LOG" >"$FRONTEND_UNUSED_LINT"; then
    FRONTEND_STATUS="findings"
  else
    : >"$FRONTEND_UNUSED_LINT"
    if [[ "$FRONTEND_TYPECHECK_EXIT" -eq 0 && "$FRONTEND_LINT_EXIT" -eq 0 && "$FRONTEND_REACHABILITY_EXIT" -eq 0 ]]; then
      if [[ "$(count_lines "$FRONTEND_UNREACHABLE")" -gt 0 || "$(count_lines "$FRONTEND_UNUSED_EXPORTS")" -gt 0 ]]; then
        FRONTEND_STATUS="findings"
      else
        FRONTEND_STATUS="clean"
      fi
    else
      FRONTEND_STATUS="validation-error"
    fi
  fi

  popd >/dev/null || exit 1
fi

BACKEND_FINDING_COUNT=$(count_lines "$BACKEND_FINDINGS")
FRONTEND_UNREACHABLE_COUNT=$(count_lines "$FRONTEND_UNREACHABLE")
FRONTEND_UNREACHABLE_APP_COUNT=$(count_lines "$FRONTEND_UNREACHABLE_APP")
FRONTEND_UNREACHABLE_TEST_COUNT=$(count_lines "$FRONTEND_UNREACHABLE_TEST")
FRONTEND_UNUSED_EXPORT_COUNT=$(count_lines "$FRONTEND_UNUSED_EXPORTS")
FRONTEND_UNUSED_LINT_COUNT=$(count_lines "$FRONTEND_UNUSED_LINT")

cat >"$SUMMARY_FILE" <<EOF
# Dead Code Analysis Summary

- Generated at: $TIMESTAMP
- Scope: active .NET solution in src/Kautuka.slnx plus src/frontend/web
- Excluded by policy: src/archive, docs, scripts unrelated to product code, tools, artifacts, generated build output, runtime data
- Strictness: conservative; framework-rooted or generated items still require manual review before removal

## Backend

- Status: $BACKEND_STATUS
- Build exit code: $BACKEND_EXIT
- High-confidence findings: $BACKEND_FINDING_COUNT
- Log: $BACKEND_LOG
- Filtered findings: $BACKEND_FINDINGS

## Frontend

- Status: $FRONTEND_STATUS
- Type-check exit code: $FRONTEND_TYPECHECK_EXIT
- Lint exit code: $FRONTEND_LINT_EXIT
- Reachability exit code: $FRONTEND_REACHABILITY_EXIT
- Unreachable files: $FRONTEND_UNREACHABLE_COUNT
- Unreachable app files: $FRONTEND_UNREACHABLE_APP_COUNT
- Unreachable test files: $FRONTEND_UNREACHABLE_TEST_COUNT
- Unused exports: $FRONTEND_UNUSED_EXPORT_COUNT
- Unused lint findings: $FRONTEND_UNUSED_LINT_COUNT
- Type-check log: $FRONTEND_TYPECHECK_LOG
- Lint log: $FRONTEND_LINT_LOG
- Reachability log: $FRONTEND_REACHABILITY_LOG
- Unreachable file report: $FRONTEND_UNREACHABLE
- Unreachable app file report: $FRONTEND_UNREACHABLE_APP
- Unreachable test file report: $FRONTEND_UNREACHABLE_TEST
- Unused export report: $FRONTEND_UNUSED_EXPORTS
- Unused lint report: $FRONTEND_UNUSED_LINT

## Interpretation

- Backend findings are filtered to warning codes already aligned with dead-code signals: CA1811, CA1823, CS0162, CS0168, CS0219, CS0414, IDE0005, IDE0051, and IDE0060.
- The backend pass is intentionally narrow. It does not claim solution-wide dead-code coverage for unused public methods, unused public types, or cross-assembly unreferences that remain reachable through contracts, DI, reflection, serialization, routing, or test projects.
- Frontend unreachable files come from the existing import-graph traversal rooted at src/main.tsx.
- Frontend unreachable files are split into app candidates versus test-only files so test reachability noise is easier to review.
- Frontend unused-export findings are conservative: they only cover reachable non-test modules with direct named exports, and they skip barrels, pages, namespace-imported files, and re-export-heavy modules.
- Frontend lint findings only include unused-variable style diagnostics already emitted by the current ESLint configuration.
- Treat DI registrations, endpoint mappings, reflection, serializers, generated contracts, React.lazy routes, feature barrels, and test-only references as manual-review areas rather than automatic deletions.
EOF

echo "Dead code analysis complete. Summary: $SUMMARY_FILE"

if [[ "$RUN_BACKEND" -eq 1 && "$BACKEND_EXIT" -ne 0 ]]; then
  exit "$BACKEND_EXIT"
fi

if [[ "$RUN_FRONTEND" -eq 1 ]]; then
  if [[ "$FRONTEND_TYPECHECK_EXIT" -ne 0 ]]; then
    exit "$FRONTEND_TYPECHECK_EXIT"
  fi

  if [[ "$FRONTEND_LINT_EXIT" -ne 0 ]]; then
    exit "$FRONTEND_LINT_EXIT"
  fi

  if [[ "$FRONTEND_REACHABILITY_EXIT" -ne 0 ]]; then
    exit "$FRONTEND_REACHABILITY_EXIT"
  fi
fi
