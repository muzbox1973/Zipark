<#
  Fails (exit 1) when the solution references a NuGet package that is not in tools/license-allowlist.json.
  Used by CI to block merges that add unreviewed dependencies.
#>
param([string]$Solution = "$PSScriptRoot\..\Zipark.sln")
$ErrorActionPreference = 'Stop'
$dotnet = if ($env:DOTNET_EXE) { $env:DOTNET_EXE } elseif (Test-Path "$env:USERPROFILE\.dotnet\dotnet.exe") { "$env:USERPROFILE\.dotnet\dotnet.exe" } else { 'dotnet' }
$allow = (Get-Content "$PSScriptRoot\license-allowlist.json" -Raw | ConvertFrom-Json).packages
$allowed = @{}
foreach ($p in $allow.PSObject.Properties) { $allowed[$p.Name.ToLowerInvariant()] = $p.Value }

$json = & $dotnet list $Solution package --include-transitive --format json | Out-String
$report = $json | ConvertFrom-Json
$bad = @()
$seen = @{}
foreach ($proj in $report.projects) {
  foreach ($fw in @($proj.frameworks)) {
    foreach ($pkg in @($fw.topLevelPackages) + @($fw.transitivePackages)) {
      if ($null -eq $pkg) { continue }
      $id = $pkg.id
      if ($seen.ContainsKey($id)) { continue }
      $seen[$id] = $true
      if ($allowed.ContainsKey($id.ToLowerInvariant())) {
        Write-Host ("OK   {0,-40} {1,-12} {2}" -f $id, $pkg.resolvedVersion, $allowed[$id.ToLowerInvariant()])
      } else {
        Write-Host ("FAIL {0,-40} {1,-12} not in license allowlist" -f $id, $pkg.resolvedVersion) -ForegroundColor Red
        $bad += $id
      }
    }
  }
}
if ($bad.Count -gt 0) { Write-Error "Unreviewed packages: $($bad -join ', ')"; exit 1 }
Write-Host "license check passed ($($seen.Count) packages)"
