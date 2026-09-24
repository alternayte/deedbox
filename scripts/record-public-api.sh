#!/usr/bin/env bash
# Adds every symbol the build reports as RS0016 (not in the declared public API) to the
# project's PublicAPI.Unshipped.txt. Review the diff: each line is new public surface.
set -euo pipefail
root="$(git rev-parse --show-toplevel)"
cd "$root"
out="$(dotnet build Deedbox.slnx --no-incremental 2>&1 || true)"
grep -E "error RS0016: Symbol '" <<< "$out" \
  | sed -E "s/.*RS0016: Symbol '(.*)' is not part of the declared public API.*\[(.*\.csproj).*/\2|\1/" \
  | sort -u \
  | while IFS='|' read -r project symbol; do
      file="$(dirname "$project")/PublicAPI.Unshipped.txt"
      grep -qxF -- "$symbol" "$file" || echo "$symbol" >> "$file"
    done
for f in src/*/PublicAPI.Unshipped.txt; do
  { head -n1 "$f"; tail -n +2 "$f" | sort -u; } > "$f.tmp" && mv "$f.tmp" "$f"
done
