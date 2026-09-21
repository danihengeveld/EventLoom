#!/usr/bin/env bash
set -euo pipefail

version="${1:?Usage: $0 <version>}"
output_directory="artifacts/packages"

mkdir -p "$output_directory"
find "$output_directory" -maxdepth 1 -type f \( -name '*.nupkg' -o -name '*.snupkg' \) -delete

projects=(
  src/EventLoom/EventLoom.csproj
  src/EventLoom.Analyzers/EventLoom.Analyzers.csproj
  src/EventLoom.AspNetCore/EventLoom.AspNetCore.csproj
  src/EventLoom.EntityFrameworkCore/EventLoom.EntityFrameworkCore.csproj
  src/EventLoom.EntityFrameworkCore.PostgreSql/EventLoom.EntityFrameworkCore.PostgreSql.csproj
  src/EventLoom.EntityFrameworkCore.Sqlite/EventLoom.EntityFrameworkCore.Sqlite.csproj
  src/EventLoom.Hosting/EventLoom.Hosting.csproj
  src/EventLoom.Testing/EventLoom.Testing.csproj
)

for project in "${projects[@]}"; do
  dotnet pack "$project" \
    --configuration Release \
    --no-build \
    --no-restore \
    --output "$output_directory" \
    -p:EventLoomPackageVersion="$version"
done
