param(
    [Parameter()][string]$RevitVersion = "2027",
    [Parameter()][string]$Configuration = "Release",
    [Parameter()][string]$OutDir = "artifacts"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$Name = "LoadingAI-Revit-$RevitVersion"
$Stage = Join-Path $Root "$OutDir\$Name"

if (Test-Path $Stage) { Remove-Item $Stage -Recurse -Force }
New-Item -ItemType Directory -Path (Join-Path $Stage "LoadingAI") | Out-Null
New-Item -ItemType Directory -Path (Join-Path $Stage "Plugins") | Out-Null

$HostBin = Join-Path $Root "src\LoadingAI\bin\$Configuration"
if (-not (Test-Path (Join-Path $HostBin "LoadingAI.dll"))) {
    throw "Host DLL missing: $HostBin\LoadingAI.dll — build first with -p:RevitVersion=$RevitVersion -p:SkipCopyAddin=true"
}

Copy-Item (Join-Path $Root "src\LoadingAI\LoadingAI.addin") (Join-Path $Stage "LoadingAI.addin")
Get-ChildItem $HostBin -Filter *.dll | Where-Object { $_.Name -notmatch '^RevitAPI' } | ForEach-Object {
    Copy-Item $_.FullName (Join-Path $Stage "LoadingAI")
}
$deps = Join-Path $HostBin "LoadingAI.deps.json"
if (Test-Path $deps) { Copy-Item $deps (Join-Path $Stage "LoadingAI") }

$Web = Join-Path $Root "src\LoadingAI\Web\dist"
if (-not (Test-Path (Join-Path $Web "index.html"))) {
    throw "Web dist missing. Run: cd web && npm ci && npm run build"
}
Copy-Item $Web (Join-Path $Stage "LoadingAI\Web\dist") -Recurse

foreach ($p in @("export-tools", "view-tools", "modeling-tools")) {
    $pluginRoot = Join-Path $Root "Plugins\$p"
    $bin = Join-Path $pluginRoot "bin\$Configuration"
    $dll = Get-ChildItem $bin -Filter "LoadingAI.Plugins.*.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $dll) { throw "Plugin DLL missing: $bin" }
    $dest = Join-Path $Stage "Plugins\$p"
    New-Item -ItemType Directory -Path $dest | Out-Null
    Copy-Item $dll.FullName $dest
    Copy-Item (Join-Path $pluginRoot "plugin.json") $dest
}

$install = @"
# LoadingAI for Revit — install into %AppData% (no admin).
param([string]`$Year = "$RevitVersion")
`$ErrorActionPreference = "Stop"
`$here = Split-Path -Parent `$MyInvocation.MyCommand.Path
`$addins = Join-Path `$env:AppData "Autodesk\Revit\Addins\`$Year"
`$pluginRoot = Join-Path `$env:AppData "LoadingAI\Plugins"
New-Item -ItemType Directory -Path `$addins -Force | Out-Null
New-Item -ItemType Directory -Path `$pluginRoot -Force | Out-Null
Copy-Item (Join-Path `$here "LoadingAI.addin") `$addins -Force
if (Test-Path (Join-Path `$addins "LoadingAI")) { Remove-Item (Join-Path `$addins "LoadingAI") -Recurse -Force }
Copy-Item (Join-Path `$here "LoadingAI") `$addins -Recurse -Force
Copy-Item (Join-Path `$here "Plugins\*") `$pluginRoot -Recurse -Force
`$legacy = Join-Path `$addins "RevitAiPoc.addin"
if (Test-Path `$legacy) {
    Remove-Item `$legacy -Force
    `$legacyDir = Join-Path `$addins "RevitAiPoc"
    if (Test-Path `$legacyDir) { Remove-Item `$legacyDir -Recurse -Force }
    Write-Host "Removed leftover RevitAiPoc (Duplicated AddInId)."
}
Write-Host "Installed LoadingAI for Revit `$Year."
Write-Host "Start Revit `$Year -> Add-Ins -> LoadingAI -> Open Chat."
"@
Set-Content -Path (Join-Path $Stage "Install.ps1") -Value $install -Encoding UTF8

New-Item -ItemType Directory -Path (Join-Path $Root $OutDir) -Force | Out-Null
$zip = Join-Path $Root "$OutDir\$Name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $Stage "*") -DestinationPath $zip
Write-Host "Packed $zip"
