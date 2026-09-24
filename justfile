# Build every package for every target framework.
build:
    dotnet build Deedbox.slnx -c Release

# Run every test on both providers (Testcontainers) and both frameworks.
test: build
    dotnet test Deedbox.slnx -c Release --no-build
    # Every SQL Server test again, on SQL Server 2025 with native json columns; one framework at a time.
    DEEDBOX_TEST_SQLSERVER_NATIVE_JSON=1 dotnet test tests/Deedbox.Tests -c Release --no-build -f net8.0 --filter "FullyQualifiedName~SqlServer"
    DEEDBOX_TEST_SQLSERVER_NATIVE_JSON=1 dotnet test tests/Deedbox.Tests -c Release --no-build -f net10.0 --filter "FullyQualifiedName~SqlServer"

# Pack every package; runs package validation.
pack: build
    dotnet pack Deedbox.slnx -c Release --no-build

# The gate: repo checks, then build, tests, pack, and the dotnet new template.
check:
    @for c in checks/*.sh; do bash "$c" || { echo "FAIL $c" >&2; exit 1; }; done
    just test
    just pack
    just template

# Generate both variants of the dotnet new template against the packed packages, and build and test them.
template:
    scripts/template-check.sh

# Refresh the docs' and README's code samples from the compiled snippets.
snippets:
    scripts/docs-snippets.sh

# Lint the docs and the README with Vale.
vale:
    scripts/vale.sh

# Build the docs site into site/dist.
docs: snippets vale
    cd site && npm ci --no-audit --no-fund && npx astro build

# Record new public API symbols in PublicAPI.Unshipped.txt after a deliberate API change.
api:
    scripts/record-public-api.sh

# Run the benchmark matrix; fails when throughput drops more than 30% below bench/baseline.json.
bench:
    mkdir -p artifacts/bench
    dotnet run --project bench/Deedbox.Benchmarks -c Release -- --out artifacts/bench/results.json --markdown artifacts/bench/results.md {{ if path_exists("bench/baseline.json") == "true" { "--baseline bench/baseline.json" } else { "" } }}
