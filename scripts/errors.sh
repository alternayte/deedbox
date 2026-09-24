#!/usr/bin/env bash
# Builds the solution and prints each distinct compiler error or warning on one short line.
# Exits non-zero when the build fails.
out="$(dotnet build "${1:-Deedbox.slnx}" 2>&1)"
status=$?
grep -oE '[A-Za-z0-9_.]+\.cs\([0-9,]+\): (error|warning) [A-Za-z0-9]+: [^[]*' <<< "$out" | sort -u
[[ $status -ne 0 ]] && grep -E 'error' <<< "$out" | grep -vE '\.cs\(' | sort -u | head -5
exit $status
