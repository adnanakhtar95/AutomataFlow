<#
.SYNOPSIS
  Creates deploy/.env.prod from deploy/.env.prod.example with fresh random secrets, and the key ring certificate
  (ADR-079). Never overwrites existing files.

.DESCRIPTION
  - database passwords (superuser, migration owner, application role), the JWT signing key (48 random bytes) and the
    key ring certificate password: 32+ random bytes each, from the OS CSPRNG;
  - deploy/secrets/keyring.pfx: a self-signed RSA-3072 certificate used ONLY to encrypt the Data Protection key ring at
    rest (it is never used for TLS). Valid for 10 years; rotation is described in docs/DEPLOYMENT.md.
  On Linux hosts, give the certificate to uid 10001 with mode 0400 (chown 10001:10001; chmod 0400) so that only the Api
  and the Worker can read it, never the supervisor (ADR-075).
  Back up deploy/.env.prod and deploy/secrets/ separately from the database: without them, encrypted data is lost.

.PARAMETER OutputEnv
  Target environment file (default deploy/.env.prod).
#>
[CmdletBinding()]
param(
    [string]$OutputEnv = 'deploy/.env.prod',
    [string]$CertificateFile = 'deploy/secrets/keyring.pfx'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envPath = if ([IO.Path]::IsPathRooted($OutputEnv)) { $OutputEnv } else { Join-Path $root $OutputEnv }
$certPath = if ([IO.Path]::IsPathRooted($CertificateFile)) { $CertificateFile } else { Join-Path $root $CertificateFile }
foreach ($existing in $envPath, $certPath) {
    if (Test-Path $existing) { throw "$existing already exists; refusing to overwrite secrets." }
}

function New-Secret([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    # URL/connection-string safe: base64 without + / =
    [Convert]::ToBase64String($buffer).Replace('+', 'A').Replace('/', 'B').TrimEnd('=')
}

$values = @{
    AF_DB_SUPERUSER_PASSWORD        = New-Secret 32
    AF_DB_OWNER_PASSWORD            = New-Secret 32
    AF_DB_APP_PASSWORD              = New-Secret 32
    AF_JWT_SIGNING_KEY              = New-Secret 48
    AF_KEYRING_CERTIFICATE_PASSWORD = New-Secret 32
}

# Key ring certificate (encryption only). .NET's CertificateRequest is available in Windows PowerShell 5.1 and pwsh.
$rsa = [Security.Cryptography.RSA]::Create(3072)
try {
    $request = New-Object Security.Cryptography.X509Certificates.CertificateRequest('CN=AutomataFlow Data Protection key ring', $rsa,
        [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
    $request.CertificateExtensions.Add((New-Object Security.Cryptography.X509Certificates.X509KeyUsageExtension(
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::KeyEncipherment, $true)))
    $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-5), [DateTimeOffset]::UtcNow.AddYears(10))
    $bytes = $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $values.AF_KEYRING_CERTIFICATE_PASSWORD)
}
finally {
    $rsa.Dispose()
}

New-Item -ItemType Directory -Force (Split-Path -Parent $certPath) | Out-Null
[IO.File]::WriteAllBytes($certPath, $bytes)

$lines = foreach ($line in Get-Content (Join-Path $root 'deploy/.env.prod.example')) {
    if ($line -match '^([A-Z0-9_]+)=' -and $values.ContainsKey($Matches[1])) { "$($Matches[1])=$($values[$Matches[1]])" } else { $line }
}
[IO.File]::WriteAllText($envPath, ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))

Write-Host "Created $envPath and $certPath."
Write-Host 'Review the remaining settings (domain, limits), then run scripts/validate-deployment.ps1.'
Write-Host 'Back up both files separately from the database; never commit them.'
