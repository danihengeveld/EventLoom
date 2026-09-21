#!/usr/bin/env bash
set -euo pipefail

version="${1:?Usage: $0 <version>}"
package_directory="$(pwd)/artifacts/packages"
work_directory="$(mktemp -d)"
trap 'rm -rf "$work_directory"' EXIT

export NUGET_PACKAGES="$work_directory/nuget-packages"

dotnet restore tests/EventLoom.PackageConsumerTests \
  --configfile tests/EventLoom.PackageConsumerTests/NuGet.Config \
  -p:EventLoomPackageVersion="$version" \
  -p:RestorePackagesWithLockFile=false
dotnet run \
  --project tests/EventLoom.PackageConsumerTests \
  --configuration Release \
  --no-restore \
  -p:EventLoomPackageVersion="$version"

dotnet new install ./templates/eventloom-api --force
dotnet new eventloom-api \
  --output "$work_directory/generated-eventloom-api" \
  --packageVersion "$version"
dotnet restore "$work_directory/generated-eventloom-api" \
  --configfile tests/EventLoom.PackageConsumerTests/NuGet.Config \
  -p:RestorePackagesWithLockFile=false
dotnet build "$work_directory/generated-eventloom-api" --no-restore
