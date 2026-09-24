#!/usr/bin/env bash
# Builds the solution and prints each distinct compiler error or warning on one short line.
dotnet build "${1:-Deedbox.slnx}" 2>&1 | grep -oE '[A-Za-z0-9_.]+\.cs\([0-9,]+\): (error|warning) [A-Z0-9]+: [^[]*' | sort -u
