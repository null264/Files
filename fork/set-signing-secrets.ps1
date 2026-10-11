# Store the fork's signing certificate in GitHub Actions secrets.
#
#   pwsh -File fork/set-signing-secrets.ps1 -PfxPath C:\Users\null\files.pfx
#
# It prompts for the .pfx password (hidden, never written to disk, never put on a command line),
# checks that the certificate subject equals the package publisher — they must be identical or the
# package cannot be signed or installed — and stores the two repository secrets that
# .github/workflows/fork-build.yml needs.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PfxPath,
    [string]$Repo = 'null264/Files'
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $PfxPath)) { throw "Certificate not found: $PfxPath" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI (gh) is not on PATH.' }

$PfxPath = (Resolve-Path -LiteralPath $PfxPath).Path
$secure = Read-Host -Prompt "Password for $PfxPath" -AsSecureString
$plain = [System.Net.NetworkCredential]::new('', $secure).Password

$flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::DefaultKeySet
$cert = [System.Security.Cryptography.X509Certificates.X509CertificateLoader]::LoadPkcs12FromFile(
    $PfxPath, $plain, $flags)

Write-Host ''
Write-Host "subject     : $($cert.Subject)"
Write-Host "thumbprint  : $($cert.Thumbprint)"
Write-Host "valid until : $($cert.NotAfter)"
Write-Host "has key     : $($cert.HasPrivateKey)"
Write-Host "key usage   : $(($cert.Extensions | Where-Object Oid -like '2.5.29.37' | Select-Object -First 1).Format($false))"

# The package publisher the build will stamp (default in fork/Fork.props).
$props = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Fork.props') -Raw
$publisher = [regex]::Match($props, 'ForkAppxPublisher[^>]*>([^<]+)<').Groups[1].Value

Write-Host "publisher   : $publisher"
if ($cert.Subject -cne $publisher) {
    Write-Warning 'MISMATCH: the certificate subject and the package publisher must be identical.'
    Write-Warning 'Either sign with the certificate that matches fork/Fork.props, or change ForkAppxPublisher there.'
} else {
    Write-Host '=> subject matches the package publisher' -ForegroundColor Green
}

Write-Host ''
Write-Host "Storing secrets on $Repo ..."
$base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($PfxPath))
$base64 | gh secret set FORK_PFX_BASE64 --repo $Repo
$plain | gh secret set FORK_PFX_PASSWORD --repo $Repo

Write-Host ''
gh secret list --repo $Repo
Write-Host ''
Write-Host 'Done. Re-run the fork-build workflow (or push to fork/main) to get a signed package.'
