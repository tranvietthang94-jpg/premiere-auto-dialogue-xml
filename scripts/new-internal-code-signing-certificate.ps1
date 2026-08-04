[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$OutputDirectory,

    [string]$Subject = 'CN=Premiere Auto Dialogue XML Internal',

    [string]$FriendlyName = 'Premiere Auto Dialogue XML Internal Code Signing',

    [ValidateRange(1, 10)]
    [int]$ValidYears = 5,

    [switch]$TrustForCurrentUser
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'Internal code-signing certificate creation is supported only on Windows.'
}

$outputFullPath = [System.IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $outputFullPath) {
    throw 'Certificate output directory already exists; this script does not overwrite.'
}

$existing = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject.Equals($Subject, [StringComparison]::OrdinalIgnoreCase) -and $_.NotAfter -gt (Get-Date) })
if ($existing.Count -ne 0) {
    throw "An unexpired current-user code-signing certificate already exists for subject '$Subject'. Reuse its thumbprint instead of creating a second identity."
}

$certificate = $null
$cerPath = Join-Path $outputFullPath 'PremiereAutoDialogueXml-Internal-CodeSigning.cer'
$manifestPath = Join-Path $outputFullPath 'certificate-manifest.json'

try {
    [System.IO.Directory]::CreateDirectory($outputFullPath) | Out-Null
    $certificate = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -FriendlyName $FriendlyName `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -KeyAlgorithm RSA `
        -KeyLength 3072 `
        -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable `
        -NotBefore (Get-Date).AddMinutes(-5) `
        -NotAfter (Get-Date).AddYears($ValidYears)

    Export-Certificate -Cert $certificate -FilePath $cerPath -Type CERT | Out-Null

    if ($TrustForCurrentUser) {
        & certutil.exe -user -f -addstore Root $cerPath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "certutil failed to add the certificate to the current-user Root store with exit code $LASTEXITCODE."
        }
        & certutil.exe -user -f -addstore TrustedPublisher $cerPath | Out-Host
        if ($LASTEXITCODE -ne 0) {
            throw "certutil failed to add the certificate to the current-user TrustedPublisher store with exit code $LASTEXITCODE."
        }
    }

    $trustedRoot = Test-Path -LiteralPath "Cert:\CurrentUser\Root\$($certificate.Thumbprint)"
    $trustedPublisher = Test-Path -LiteralPath "Cert:\CurrentUser\TrustedPublisher\$($certificate.Thumbprint)"
    if ($TrustForCurrentUser -and (-not $trustedRoot -or -not $trustedPublisher)) {
        throw 'The public certificate was not installed in both current-user trust stores.'
    }

    $rsa = [System.Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($certificate)
    try {
        $keySize = $rsa.KeySize
    }
    finally {
        $rsa.Dispose()
    }

    $manifest = [ordered]@{
        schemaVersion = '1.0'
        createdAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        purpose = 'Internal Authenticode code signing only'
        subject = $certificate.Subject
        friendlyName = $certificate.FriendlyName
        thumbprint = $certificate.Thumbprint
        serialNumber = $certificate.SerialNumber
        notBeforeUtc = $certificate.NotBefore.ToUniversalTime().ToString('O')
        notAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
        signatureAlgorithm = $certificate.SignatureAlgorithm.FriendlyName
        publicKeyAlgorithm = $certificate.PublicKey.Oid.FriendlyName
        publicKeyBits = $keySize
        cerFile = [System.IO.Path]::GetFileName($cerPath)
        cerSha256 = (Get-FileHash -LiteralPath $cerPath -Algorithm SHA256).Hash
        privateKeyExported = $false
        privateKeyStore = 'Cert:\CurrentUser\My'
        trustedRootCurrentUser = $trustedRoot
        trustedPublisherCurrentUser = $trustedPublisher
        timestampService = $null
    }
    $manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8

    [pscustomobject]@{
        OutputDirectory = $outputFullPath
        CertificatePath = $cerPath
        ManifestPath = $manifestPath
        Subject = $certificate.Subject
        Thumbprint = $certificate.Thumbprint
        NotAfter = $certificate.NotAfter
        TrustedForCurrentUser = ($trustedRoot -and $trustedPublisher)
        PrivateKeyExported = $false
    }
}
catch {
    if ($null -ne $certificate) {
        foreach ($store in @('My', 'Root', 'TrustedPublisher')) {
            $candidate = "Cert:\CurrentUser\$store\$($certificate.Thumbprint)"
            if (Test-Path -LiteralPath $candidate) {
                Remove-Item -LiteralPath $candidate -Force
            }
        }
    }
    if (Test-Path -LiteralPath $outputFullPath -PathType Container) {
        Remove-Item -LiteralPath $outputFullPath -Recurse -Force
    }
    throw
}
