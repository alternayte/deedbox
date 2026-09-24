#!/usr/bin/env bash
# check: docs-snippets-current
# born: 2026-09-24
# failure: a docs page or the README showed code that no longer compiles against the API
# rule: every code sample in site/src/snippets and README.md matches the compiled snippets in site/snippets
set -euo pipefail

root="$(git rev-parse --show-toplevel)"
cd "$root"
scripts/docs-snippets.sh
if ! git diff --quiet -- site/src/snippets README.md || [[ -n "$(git ls-files --others --exclude-standard site/src/snippets)" ]]; then
  echo "rule: docs snippets are stale; run scripts/docs-snippets.sh and commit the result" >&2
  git status --short -- site/src/snippets README.md >&2
  exit 1
fi
