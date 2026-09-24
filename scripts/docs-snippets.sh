#!/usr/bin/env bash
# Refreshes every code sample in the docs site and the README from the compiled snippets in site/snippets.
# Each snippet gets a partial at site/src/snippets/<name>.md, which docs pages import.
set -euo pipefail
root="$(git rev-parse --show-toplevel)"
cd "$root"

names="$(grep -rhoE 'begin-snippet: [A-Za-z0-9_-]+' site/snippets --include='*.cs' | sed 's/begin-snippet: //' | sort -u)"
mkdir -p site/src/snippets
for name in $names; do
  file="site/src/snippets/$name.md"
  [[ -f "$file" ]] || printf '<!-- snippet: %s -->\n<!-- endSnippet -->\n' "$name" > "$file"
done
for file in site/src/snippets/*.md; do
  name="$(basename "$file" .md)"
  [[ "$name" == "benchmarks" ]] && continue
  grep -qx "$name" <<< "$names" || { echo "rule: $file has no snippet named $name in site/snippets" >&2; exit 1; }
done

cp bench/results.md site/src/snippets/benchmarks.md
dotnet tool restore >/dev/null
dotnet tool run mdsnippets site >/dev/null
dotnet tool run mdsnippets . >/dev/null
