param(
    [Parameter(Mandatory = $true)][string]$OutPath,
    [Parameter(Mandatory = $true)][string[]]$Dlls
)

$ErrorActionPreference = "Stop"
$hashes = foreach ($d in $Dlls) {
    if (-not (Test-Path -LiteralPath $d)) { throw "Missing plugin DLL for shipped hashes: $d" }
    (Get-FileHash -LiteralPath $d -Algorithm SHA256).Hash.ToLowerInvariant()
}
$dir = Split-Path -Parent $OutPath
if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$json = "[" + (($hashes | ForEach-Object { '"' + $_ + '"' }) -join ",") + "]"
Set-Content -LiteralPath $OutPath -Value $json -Encoding ascii
