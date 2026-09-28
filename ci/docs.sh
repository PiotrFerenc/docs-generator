#!/usr/bin/env bash
# Generates business docs for the application repo this is run from (repo root).
#
# Env:
#   DOCGEN_HOME        path to the docgen repo (built here); alternatively `docgen` on PATH (dotnet tool)
#   DOCGEN_CONFIG      appsettings.json of the application repo       (default: appsettings.json)
#   DOCGEN_WORK_DIR    cache dir; persist it between CI runs         (default: .docgen)
#   DOCGEN_OUTPUT_DIR  rendered Markdown                             (default: .docgen/docs-out)
#   DOCGEN_MODE        mr | main                                     (default: detected from GitLab/Azure vars)
#   DOCS_COMMIT_DIR    mr: copy rendered docs here and show git diff (default: empty = skip)
#   DOCGEN_PUBLISH     main: 1 = docgen publish (Confluence)         (default: 1)
#   DOCGEN_SEARCH      main: 1 = docgen search-index (Qdrant)        (default: 0)
# Secrets: DOCGEN__<Section>__<Key>, e.g. DOCGEN__HandlerCard__ApiKey, DOCGEN__Confluence__ApiKey.
set -euo pipefail

CONFIG="${DOCGEN_CONFIG:-appsettings.json}"
export DOCGEN__Generator__WorkDir="$(realpath -m "${DOCGEN_WORK_DIR:-.docgen}")"
export DOCGEN__Generator__OutputDir="$(realpath -m "${DOCGEN_OUTPUT_DIR:-.docgen/docs-out}")"

if [[ -n "${DOCGEN_HOME:-}" ]]; then
  dotnet build "$DOCGEN_HOME/src/DocGen.Cli/DocGen.Cli.csproj" -c Release -o "$DOCGEN_HOME/.bin" -v quiet -nologo
  docgen() { dotnet "$DOCGEN_HOME/.bin/docgen.dll" --config "$CONFIG" "$@"; }
elif command -v docgen >/dev/null; then
  docgen() { command docgen --config "$CONFIG" "$@"; }
else
  echo "docs.sh: set DOCGEN_HOME or install docgen on PATH" >&2; exit 2
fi

MODE="${DOCGEN_MODE:-}"
if [[ -z "$MODE" ]]; then
  if [[ -n "${CI_MERGE_REQUEST_IID:-}${SYSTEM_PULLREQUEST_PULLREQUESTID:-}" ]]; then MODE=mr; else MODE=main; fi
fi
echo "docs.sh: mode=$MODE config=$CONFIG out=$DOCGEN__Generator__OutputDir"

docgen run

if [[ "$MODE" == mr ]]; then
  if [[ -n "${DOCS_COMMIT_DIR:-}" ]]; then
    rm -rf "$DOCS_COMMIT_DIR"
    mkdir -p "$DOCS_COMMIT_DIR"
    cp -r "$DOCGEN__Generator__OutputDir"/. "$DOCS_COMMIT_DIR"/
    git add -N "$DOCS_COMMIT_DIR"   # make new pages visible in the diff
    echo "── Zmiany w dokumentacji (reguły biznesowe) ──"
    git --no-pager diff --stat -- "$DOCS_COMMIT_DIR"
  fi
else
  [[ "${DOCGEN_PUBLISH:-1}" == 1 ]] && docgen publish
  [[ "${DOCGEN_SEARCH:-0}" == 1 ]] && docgen search-index
fi
exit 0
