#!/usr/bin/env bash
# Tests, packs and tags a SqliteReader release. The package must then be uploaded manually on nuget.org.
# Usage: ./release.sh
set -euo pipefail
cd "$(dirname "$0")"

project=Source/SqliteReader/SqliteReader.csproj
version=$(dotnet msbuild "$project" -getProperty:Version)

dotnet test Source/SqliteReader.sln -c Release
dotnet pack "$project" -c Release

git tag "$version"
git push --tags

echo
echo "Upload this package at https://www.nuget.org/packages/manage/upload:"
echo "  Source/SqliteReader/bin/Release/SqliteReader.$version.nupkg"
echo "Symbols package (optional):"
echo "  Source/SqliteReader/bin/Release/SqliteReader.$version.snupkg"
