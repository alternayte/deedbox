#!/usr/bin/env bash
# Installs the packed dotnet new template into an isolated hive, generates both database variants against the
# locally packed Deedbox packages, and builds and tests them. Run after `just pack`.
set -euo pipefail
root="$(git rev-parse --show-toplevel)"
cd "$root"
feed="$root/artifacts/package/release"
# Outside the repo, so the generated projects do not inherit its Directory.Build.props and package versions.
work="$(mktemp -d)/deedbox-template-check"
mkdir -p "$work"
trap 'rm -rf "$(dirname "$work")"' EXIT
template="$(ls "$feed"/Deedbox.Templates.*.nupkg | head -1)"
dotnet new install "$template" --debug:custom-hive "$work/hive" >/dev/null

cat > "$work/nuget.config" <<CONFIG
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$feed" />
    <add key="nuget" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
CONFIG

for database in postgres sqlserver; do
  dotnet new deedbox --database "$database" -o "$work/$database/Shop" --debug:custom-hive "$work/hive" >/dev/null
  cp "$work/nuget.config" "$work/$database/Shop/nuget.config"
  (cd "$work/$database/Shop" && dotnet test Shop.slnx -v quiet --nologo | tail -3)
done
