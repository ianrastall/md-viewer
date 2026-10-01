# Code signing for md-viewer builds; dot-sourced by release.ps1 and package.ps1.
# Uses a certificate already in the Windows certificate store: the one named by thumbprint, or else the
# newest valid code-signing certificate whose subject matches the package publisher (CN=Ian Rastall).
# The private key is never exported or copied, and certificate trust is never changed here.

function Get-SignTool {
    $bin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $tool = Get-ChildItem -LiteralPath $bin -Directory -ErrorAction SilentlyContinue |
        Where-Object Name -Match '^10\.0\.\d+\.\d+$' |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName 'x64\signtool.exe' } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $tool) { throw 'SignTool was not found. Install the Windows SDK signing tools.' }
    $tool
}

function Get-PackagePublisher([string]$Root) {
    ([xml](Get-Content -LiteralPath (Join-Path $Root 'packaging\AppxManifest.xml') -Raw)).Package.Identity.Publisher
}

# Returns the certificate to sign with, or $null when there is none (an unsigned build).
function Resolve-SigningCertificate([string]$Thumbprint, [string]$Publisher) {
    $now = Get-Date
    $usable = {
        param($certificate)
        $certificate.HasPrivateKey -and $certificate.NotBefore -le $now -and $certificate.NotAfter -gt $now -and
            ($certificate.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')
    }
    if ($Thumbprint) {
        $clean = ($Thumbprint -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
        $certificate = @('Cert:\CurrentUser\My', 'Cert:\LocalMachine\My') | ForEach-Object { Get-Item -LiteralPath "$_\$clean" -ErrorAction SilentlyContinue } | Select-Object -First 1
        if (-not $certificate) { throw "No certificate with thumbprint $clean is in your certificate store." }
        if (-not (& $usable $certificate)) { throw "Certificate $clean cannot sign code: it needs a private key, the code-signing purpose, and to be within its validity dates." }
        return $certificate
    }
    Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
        Where-Object { $_.Subject -eq $Publisher -and (& $usable $_) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
}

function Get-SignArguments($Certificate, [string]$TimestampUrl) {
    $arguments = @('sign', '/fd', 'SHA256', '/sha1', $Certificate.Thumbprint)
    if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    $arguments
}

function Invoke-CodeSigning($Certificate, [string]$TimestampUrl, [string[]]$Files) {
    $signtool = Get-SignTool
    & $signtool @(Get-SignArguments $Certificate $TimestampUrl) /q @Files
    if ($LASTEXITCODE -ne 0) { throw "SignTool could not sign: $($Files -join ', ')" }
}

# "Valid" when this machine trusts the certificate; a self-signed certificate that is not trusted here
# reports UnknownError even though the signature itself is intact.
function Show-Signature([string]$Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $Path
    $stamp = if ($signature.TimeStamperCertificate) { 'timestamped' } else { 'not timestamped' }
    Write-Host ("Signature {0}: {1}, {2}, {3}" -f (Split-Path $Path -Leaf), $signature.Status, $signature.SignerCertificate.Subject, $stamp)
}
