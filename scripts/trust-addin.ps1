# Local-dev only. Re-asserts per-user Always Load for LoadingAI and Authenticode-signs
# the deployed host DLL so Revit 2024-2027 does not re-prompt after CopyAddin.
# HKCU CodeSigning DWORD is GUID-only; Revit still re-prompts when the unsigned
# file hash changes. Signing + CurrentUser TrustedPublisher is the Autodesk path.
# Does not disable add-in security globally. CI must pass -p:SkipTrustAddin=true
# (already implied by SkipCopyAddin). Do not commit the generated cert.
param(
    [Parameter()][string]$RevitVersion = "2027",
    [Parameter()][string]$DllPath = "",
    [Parameter()][string]$AddInId = "8F2C4E2A-9B3D-4E2A-A5F1-7D1A3B9C0E12",
    [Parameter()][switch]$SkipSign
)

$ErrorActionPreference = "Stop"
$Subject = "CN=LoadingAI Local Dev"
$Guid = $AddInId.ToLowerInvariant()

function Set-CodeSigningAlwaysLoad {
    param([string]$Year, [string]$Name)

    $revitKey = "HKCU:\SOFTWARE\Autodesk\Revit"
    $names = @()
    if ($Year) { $names += "Autodesk Revit $Year" }
    if (Test-Path $revitKey) {
        Get-ChildItem $revitKey -ErrorAction SilentlyContinue |
            Where-Object { $_.PSChildName -like "Autodesk Revit *" } |
            ForEach-Object { $names += $_.PSChildName }
    }

    $names | Select-Object -Unique | ForEach-Object {
        $cs = Join-Path $revitKey "$_\CodeSigning"
        New-Item -Path $cs -Force | Out-Null
        New-ItemProperty -Path $cs -Name $Name -PropertyType DWord -Value 1 -Force | Out-Null
        Write-Host "CodeSigning Always Load: $cs\$Name = 1"
    }
}

function Test-IsCodeSigningCert {
    param($Certificate)
    if (-not $Certificate -or -not $Certificate.HasPrivateKey) { return $false }
    $eku = @($Certificate.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId })
    # 1.3.6.1.5.5.7.3.3 = codeSigning. Empty EKU on some self-signed certs still signs.
    return ($eku.Count -eq 0) -or ($eku -contains "1.3.6.1.5.5.7.3.3")
}

function New-LocalCodeSigningCert {
    # No Cert: PSDrive — MSBuild's Windows PowerShell 5.1 often has none.
    $rsa = [System.Security.Cryptography.RSA]::Create(2048)
    $req = New-Object System.Security.Cryptography.X509Certificates.CertificateRequest(
        $Subject,
        $rsa,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $usage = New-Object System.Security.Cryptography.X509Certificates.X509KeyUsageExtension(
        [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true)
    $req.CertificateExtensions.Add($usage)
    $oids = New-Object System.Security.Cryptography.OidCollection
    [void]$oids.Add((New-Object System.Security.Cryptography.Oid "1.3.6.1.5.5.7.3.3"))
    $eku = New-Object System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension($oids, $true)
    $req.CertificateExtensions.Add($eku)
    $raw = $req.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddYears(5))
    $pfx = $raw.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, "")
    $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::PersistKeySet -bor
             [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::UserKeySet -bor
             [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable
    $persisted = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($pfx, "", $flags)
    $persisted.FriendlyName = "LoadingAI Local Dev"
    return $persisted
}

function Get-OrCreateCodeSigningCert {
    $now = Get-Date
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        "My",
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $existing = $store.Certificates |
            Where-Object {
                $_.Subject -eq $Subject -and
                (Test-IsCodeSigningCert $_) -and
                $_.NotAfter -gt $now.AddDays(30)
            } |
            Sort-Object NotAfter -Descending |
            Select-Object -First 1
        if ($existing) { return $existing }

        Write-Host "Creating CurrentUser code-signing cert: $Subject"
        $created = New-LocalCodeSigningCert
        $store.Add($created)
        return $created
    }
    finally {
        $store.Close()
    }
}

function Add-CurrentUserStore {
    param(
        [Parameter(Mandatory)][string]$StoreName,
        [Parameter(Mandatory)]$Certificate
    )
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store(
        $StoreName,
        [System.Security.Cryptography.X509Certificates.StoreLocation]::CurrentUser)
    $store.Open([System.Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try {
        $dup = $store.Certificates | Where-Object { $_.Thumbprint -eq $Certificate.Thumbprint }
        if ($dup) { return }
        $store.Add($Certificate)
        Write-Host "Imported $($Certificate.Thumbprint) into CurrentUser\$StoreName"
    }
    finally {
        $store.Close()
    }
}

Set-CodeSigningAlwaysLoad -Year $RevitVersion -Name $Guid

if ($SkipSign) { return }

if (-not $DllPath) {
    $DllPath = Join-Path $env:AppData "Autodesk\Revit\Addins\$RevitVersion\LoadingAI\LoadingAI.dll"
}

if (-not (Test-Path $DllPath)) {
    Write-Host "Skip Authenticode: DLL not found at $DllPath"
    return
}

try {
    $cert = Get-OrCreateCodeSigningCert
    # Revit checks Trusted Publishers. Windows Authenticode Status=Valid also
    # needs the self-signed cert as CurrentUser\Root. Both stores are per-user.
    Add-CurrentUserStore -StoreName "TrustedPublisher" -Certificate $cert
    Add-CurrentUserStore -StoreName "Root" -Certificate $cert

    $sig = Set-AuthenticodeSignature -FilePath $DllPath -Certificate $cert -HashAlgorithm SHA256
    Write-Host "Signed $DllPath status=$($sig.Status) thumbprint=$($cert.Thumbprint)"
    if ($sig.Status -eq "NotSigned") {
        Write-Warning "Authenticode did not attach: $($sig.StatusMessage)"
    }
}
catch {
    Write-Warning "Could not sign $DllPath (close Revit if the DLL is locked): $($_.Exception.Message)"
}
