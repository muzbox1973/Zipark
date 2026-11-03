<#
  Zipark release build.
    pwsh build/build-release.ps1 [-SevenZipDir "C:\Program Files\7-Zip"] [-SkipTests]
  Produces: artifacts/publish (self-contained app), artifacts/Zipark-<version>-x64-portable.zip,
            artifacts/Zipark-<version>-x64.msi, artifacts/sbom.json
  The 7-Zip engine (7z.exe/7z.dll, LGPL-2.1-or-later + unRAR restriction) is copied unmodified together with
  its License.txt; see THIRD_PARTY_NOTICES.md for the redistribution obligations.
#>
param(
  [string]$SevenZipDir = "$env:ProgramFiles\7-Zip",
  [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$art = Join-Path $root 'artifacts'
$pub = Join-Path $art 'publish'
$dotnet = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } elseif (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { "$env:USERPROFILE\.dotnet\dotnet.exe" } else { 'dotnet' }
if (Test-Path $pub) { Remove-Item $pub -Recurse -Force }
New-Item -ItemType Directory -Force $pub | Out-Null

# Offline HTML manual + version history, license text for the MSI dialog
python (Join-Path $root 'tools\docs\build-docs.py')
if ($LASTEXITCODE -ne 0) { throw 'docs build failed' }
python (Join-Path $root 'tools\branding\make-license-rtf.py')

if (-not $SkipTests) {
  & $dotnet test "$root\Zipark.sln" -c Release
  if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
}

foreach ($p in 'src\Zipark.Desktop', 'src\Zipark.Worker', 'src\Zipark.VirtualFileSystem.Host') {
  & $dotnet publish (Join-Path $root $p) -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o $pub
  if ($LASTEXITCODE -ne 0) { throw "publish failed: $p" }
}

# Engine (unmodified 7-Zip binaries + their license)
$engine = Join-Path $pub 'engine'
New-Item -ItemType Directory -Force $engine | Out-Null
foreach ($f in '7z.exe', '7z.dll', 'License.txt', 'readme.txt') {
  $s = Join-Path $SevenZipDir $f
  if (-not (Test-Path $s)) { throw "7-Zip file missing: $s" }
  Copy-Item $s (Join-Path $engine $f)
}
Copy-Item "$root\LICENSE" "$pub\LICENSE.txt" -Force
Copy-Item "$root\THIRD_PARTY_NOTICES.md", "$root\README.md", "$root\CHANGELOG.md" $pub -Force
Copy-Item "$root\docs\USER_MANUAL.md" $pub -Force
Get-ChildItem $pub -Filter *.pdb -Recurse | Remove-Item

# SBOM-style inventory of NuGet dependencies (license check runs in CI)
& $dotnet list "$root\Zipark.sln" package --include-transitive --format json | Out-File -Encoding utf8 "$art\sbom.json"

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version
Compress-Archive -Path "$pub\*" -DestinationPath "$art\Zipark-$version-x64-portable.zip" -Force
& $dotnet build "$root\installer\Zipark.Installer\Zipark.Installer.wixproj" -c Release -p:PublishDir="$pub\" -o $art
if ($LASTEXITCODE -ne 0) { throw 'MSI build failed' }
Get-ChildItem $art -File | Select-Object Name, Length
