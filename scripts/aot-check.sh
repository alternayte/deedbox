#!/usr/bin/env bash
# Publishes tests/Deedbox.Aot as a native AOT binary and runs it against a throwaway Postgres. A trim or AOT warning
# from Deedbox fails the publish, because warnings are errors; a missing JSON contract or reflection path fails the run.
set -euo pipefail
root="$(git rev-parse --show-toplevel)"
cd "$root"

out="$root/artifacts/aot"
rm -rf "$out"
dotnet publish tests/Deedbox.Aot/Deedbox.Aot.csproj -c Release -f net10.0 -o "$out" --nologo -v quiet

container="$(docker run -d --rm -e POSTGRES_PASSWORD=aot -p 127.0.0.1::5432 postgres:17-alpine)"
trap 'docker rm -f "$container" >/dev/null 2>&1 || true' EXIT
port="$(docker port "$container" 5432/tcp | head -1 | sed 's/.*://')"
until docker exec "$container" pg_isready -U postgres >/dev/null 2>&1; do sleep 0.5; done

DEEDBOX_AOT_CONNECTION="Host=127.0.0.1;Port=$port;Username=postgres;Password=aot;Database=postgres" "$out/Deedbox.Aot"
