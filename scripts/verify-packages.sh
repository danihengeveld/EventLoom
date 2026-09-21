#!/usr/bin/env bash
set -euo pipefail

version="${1:?Usage: $0 <version> [package-directory]}"
package_directory="${2:-artifacts/packages}"
temporary_directory="$(mktemp -d)"
trap 'rm -rf "$temporary_directory"' EXIT

package_ids=(
  EventLoom
  EventLoom.Analyzers
  EventLoom.AspNetCore
  EventLoom.EntityFrameworkCore
  EventLoom.EntityFrameworkCore.PostgreSql
  EventLoom.EntityFrameworkCore.Sqlite
  EventLoom.Hosting
  EventLoom.Testing
)

for extension in nupkg; do
  for package_id in "${package_ids[@]}"; do
    printf '%s.%s.%s\n' "$package_id" "$version" "$extension"
  done | sort > "$temporary_directory/expected-$extension.txt"

  find "$package_directory" -maxdepth 1 -name "*.$extension" -exec basename {} \; \
    | sort > "$temporary_directory/actual-$extension.txt"
  diff --unified "$temporary_directory/expected-$extension.txt" "$temporary_directory/actual-$extension.txt"
done

for package_id in "${package_ids[@]}"; do
  if [[ "$package_id" != 'EventLoom.Analyzers' ]]; then
    printf '%s.%s.snupkg\n' "$package_id" "$version"
  fi
done | sort > "$temporary_directory/expected-snupkg.txt"
find "$package_directory" -maxdepth 1 -name '*.snupkg' -exec basename {} \; \
  | sort > "$temporary_directory/actual-snupkg.txt"
diff --unified "$temporary_directory/expected-snupkg.txt" "$temporary_directory/actual-snupkg.txt"

for package in "$package_directory"/*.nupkg; do
  unzip -Z1 "$package" | grep --fixed-strings --line-regexp 'README.md' > /dev/null
  unzip -Z1 "$package" | grep --fixed-strings --line-regexp 'eventloom-icon.png' > /dev/null
  unzip -p "$package" '*.nuspec' | grep --fixed-strings "<version>$version</version>" > /dev/null
  unzip -p "$package" '*.nuspec' | grep --fixed-strings '<icon>eventloom-icon.png</icon>' > /dev/null
  unzip -p "$package" '*.nuspec' | grep --fixed-strings '<repository type="git"' > /dev/null
done
