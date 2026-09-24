# Build every package for every target framework.
build:
    dotnet build Deedbox.slnx -c Release

# Run every test on both providers (Testcontainers) and both frameworks.
test: build
    dotnet test Deedbox.slnx -c Release --no-build

# Pack every package; runs package validation.
pack: build
    dotnet pack Deedbox.slnx -c Release --no-build

# The gate: repo checks, then build, tests and pack.
check:
    @for c in checks/*.sh; do bash "$c" || { echo "FAIL $c" >&2; exit 1; }; done
    just test
    just pack
