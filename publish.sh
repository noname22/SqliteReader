#!/usr/bin/env bash
# Tests, packs, tags and publishes SqliteReader to nuget.org.
# Usage: ./publish.sh <nuget-api-key>
set -euo pipefail
cd "$(dirname "$0")"

if [ $# -ne 1 ]; then
    echo "Usage: $0 <nuget-api-key>" >&2
    exit 1
fi

project=Source/SqliteReader/SqliteReader.csproj
version=$(dotnet msbuild "$project" -getProperty:Version)

dotnet test Source/SqliteReader.sln -c Release
dotnet pack "$project" -c Release

git tag "$version"
git push --tags
dotnet nuget push "Source/SqliteReader/bin/Release/SqliteReader.$version.nupkg" --api-key "$1" --source https://api.nuget.org/v3/index.json
