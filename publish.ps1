$ErrorActionPreference = 'Stop'

Select-Xml -Path ./Source/SqliteReader/SqliteReader.csproj -XPath '/Project/PropertyGroup/Version' | ForEach-Object { $version = $_.Node.InnerXML }

dotnet test ./Source/SqliteReader.sln -c Release
if ($LASTEXITCODE -ne 0) { throw "Tests failed" }

dotnet pack ./Source/SqliteReader/SqliteReader.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Pack failed" }

git tag $version
git push --tags
dotnet nuget push ./Source/SqliteReader/bin/Release/SqliteReader.$version.nupkg --api-key $args[0] --source https://api.nuget.org/v3/index.json
