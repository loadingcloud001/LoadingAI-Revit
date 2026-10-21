# Expand the four release zips into installer/payload/<year> for Inno Setup.
param(
    [Parameter()][string]$Dist = "dist",
    [Parameter()][string]$Payload = "installer\payload"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$DistPath = if ([System.IO.Path]::IsPathRooted($Dist)) { $Dist } else { Join-Path $Root $Dist }
$PayloadPath = if ([System.IO.Path]::IsPathRooted($Payload)) { $Payload } else { Join-Path $Root $Payload }

foreach ($year in @("2024", "2025", "2026", "2027")) {
    $zip = Get-ChildItem -Path $DistPath -Filter "LoadingAI-Revit-$year.zip" -Recurse -File | Select-Object -First 1
    if (-not $zip) { throw "Missing LoadingAI-Revit-$year.zip under $DistPath" }
    $dest = Join-Path $PayloadPath $year
    if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
    New-Item -ItemType Directory -Path $dest -Force | Out-Null
    Expand-Archive -Path $zip.FullName -DestinationPath $dest -Force
    foreach ($required in @("LoadingAI.addin", "LoadingAI", "Plugins")) {
        if (-not (Test-Path (Join-Path $dest $required))) {
            throw "Zip $($zip.Name) is missing $required"
        }
    }
    Write-Host "Staged Revit $year from $($zip.FullName)"
}
