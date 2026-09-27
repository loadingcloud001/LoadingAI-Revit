param(
    [Parameter(Mandatory = $true)][string]$OutPath,
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Dlls
)

$ErrorActionPreference = "Stop"
$hashes = foreach ($d in $Dlls) {
    if (-not (Test-Path -LiteralPath $d)) { throw "Missing plugin DLL for shipped hashes: $d" }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.IO.File]::ReadAllBytes($d)
        ([System.BitConverter]::ToString($sha.ComputeHash($bytes))).Replace("-", "").ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}
$dir = Split-Path -Parent $OutPath
if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
$json = "[" + (($hashes | ForEach-Object { '"' + $_ + '"' }) -join ",") + "]"
Set-Content -LiteralPath $OutPath -Value $json -Encoding ascii
