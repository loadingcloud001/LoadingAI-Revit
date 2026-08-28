# Generates src/LoadingAI.Core/Llm/DefaultLlmKey.g.cs from
# LOADINGAI_DEFAULT_KEY or local/default-llm.json. Never prints the key.
param()
$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
$key = $env:LOADINGAI_DEFAULT_KEY
$jsonPath = Join-Path $Root "local\default-llm.json"
if (-not $key -and (Test-Path $jsonPath)) {
    $obj = Get-Content $jsonPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $key = [string]$obj.key
}
$out = Join-Path $Root "src\LoadingAI.Core\Llm\DefaultLlmKey.g.cs"
if ([string]::IsNullOrWhiteSpace($key)) { exit 0 }

$csPath = Join-Path $Root "src\LoadingAI.Core\Llm\DefaultLlm.cs"
$cs = Get-Content $csPath -Raw -Encoding UTF8
if ($cs -notmatch 'internal const string Salt = "([^"]+)"') {
    throw "DefaultLlmKey.Salt not found in DefaultLlm.cs"
}
$salt = [System.Text.Encoding]::UTF8.GetBytes($Matches[1])
$bytes = [System.Text.Encoding]::UTF8.GetBytes($key)
$parts = New-Object System.Collections.Generic.List[string]
for ($i = 0; $i -lt $bytes.Length; $i++) {
    $x = $bytes[$i] -bxor $salt[$i % $salt.Length]
    [void]$parts.Add(("0x{0:X2}" -f $x))
}
$hex = [string]::Join(", ", $parts)
$cs = @"
#nullable enable
namespace LoadingAI.Llm;
internal static partial class DefaultLlmKey
{
    static partial void GetPacked(ref byte[]? packed)
    {
        packed = new byte[] { $hex };
    }
}
"@
$dir = Split-Path $out
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
Set-Content -Path $out -Value $cs -Encoding UTF8
Write-Host "Wrote DefaultLlmKey.g.cs ($($bytes.Length) bytes packed)"
