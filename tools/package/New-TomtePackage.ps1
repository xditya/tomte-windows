<#
.SYNOPSIS
Builds Tomte.Shell into a signed MSIX, writes a matching .appinstaller file and installs the package for the current user.

.DESCRIPTION
Signing uses a self-signed code-signing certificate kept in the current user's certificate store
(Cert:\CurrentUser\My). The script reuses it when it exists and creates it otherwise. The private key
never leaves the store: no .pfx file and no password are written anywhere, so there is nothing to commit.
To delete the certificate, remove it from Cert:\CurrentUser\My and Cert:\LocalMachine\TrustedPeople.

Windows only installs a package whose signer it trusts. The first run copies the public certificate into
Cert:\LocalMachine\TrustedPeople, which shows one UAC prompt. Later runs skip that step.

Output goes to tools\package\bin\ (ignored by git).

.PARAMETER CertSubject
Certificate subject. It is also the package Publisher, so it must match the production certificate
(or the Store-assigned publisher) when one is chosen.

.PARAMETER Version
Package version, four parts.

.PARAMETER AppInstallerBaseUri
Folder URL where the .msix and .appinstaller will be hosted. Written into the .appinstaller file.

.PARAMETER Register
Still builds and signs the .msix, but installs by registering the unpacked folder instead (needs
Developer Mode, no UAC prompt, no certificate trust). For local work when the machine trust step is not wanted.

.PARAMETER NoInstall
Stops after signing the .msix and writing the .appinstaller file, without installing anything. For release builds.

.EXAMPLE
powershell -File tools\package\New-TomtePackage.ps1
#>
param(
    [string]$CertSubject = 'CN=Tomte Dev',
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$Version = '0.2.1.0',
    [string]$AppInstallerBaseUri = 'https://example.invalid/tomte/',
    [switch]$Register,
    [switch]$NoInstall
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\..\.."
$out = Join-Path $PSScriptRoot 'bin'
$stage = Join-Path $out 'stage'
$name = 'Tomte'
$msix = Join-Path $out "${name}_${Version}_x64.msix"
if (-not $AppInstallerBaseUri.EndsWith('/')) { $AppInstallerBaseUri += '/' }

function Find-SdkTool([string]$exe) {
    $tool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$exe" -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Directory.Parent.Name } | Select-Object -Last 1
    if (-not $tool) { throw "$exe not found. Install the Windows 10/11 SDK." }
    $tool.FullName
}
$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = Find-SdkTool 'signtool.exe'

# 1. Publish self-contained, so the package does not depend on a .NET runtime being installed.
# A copy registered with -Register runs from $stage and locks it, so stop it first.
Get-Process Tomte.Shell -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$stage\*" } |
    Stop-Process -Force -PassThru | Wait-Process -Timeout 10
# Wait-Process can return a few ms before the image sections are unmapped, so retry the delete.
foreach ($try in 1..10) {
    try { if (Test-Path $stage) { Remove-Item $stage -Recurse -Force -ErrorAction Stop }; break }
    catch { if ($try -eq 10) { throw }; Start-Sleep -Milliseconds 300 }
}
dotnet publish "$root\src\Tomte.Shell\Tomte.Shell.csproj" -c Release -r win-x64 --self-contained -o $stage --nologo
if ($LASTEXITCODE) { throw 'dotnet publish failed' }

# 2. Manifest with this run's publisher and version.
[xml]$manifest = Get-Content "$PSScriptRoot\Package.appxmanifest"
$manifest.Package.Identity.Publisher = $CertSubject
$manifest.Package.Identity.Version = $Version
$manifest.Save((Join-Path $stage 'AppxManifest.xml'))

# 3. Logos. Real artwork goes in src\Tomte.Shell\Assets with the file names below.
#    Until then each logo is a flat magenta square with "TMP" on it, so nobody ships it by mistake.
$assets = New-Item (Join-Path $stage 'Assets') -ItemType Directory -Force
$logos = @{ 'StoreLogo.png' = 50; 'Square150x150Logo.png' = 150; 'Square44x44Logo.png' = 44 }
$realAssets = "$root\src\Tomte.Shell\Assets"
Add-Type -AssemblyName System.Drawing
foreach ($logo in $logos.GetEnumerator()) {
    $real = Join-Path $realAssets $logo.Key
    if (Test-Path $real) { Copy-Item $real $assets; continue }
    Write-Warning "Placeholder logo: $($logo.Key)"
    $bitmap = New-Object System.Drawing.Bitmap $logo.Value, $logo.Value
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.Clear([System.Drawing.Color]::Magenta)
    $font = New-Object System.Drawing.Font 'Segoe UI', ($logo.Value / 4), ([System.Drawing.FontStyle]::Bold)
    $graphics.DrawString('TMP', $font, [System.Drawing.Brushes]::Black, 2, 2)
    $bitmap.Save((Join-Path $assets $logo.Key), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

# 4. Pack.
$log = & $makeappx pack /d $stage /p $msix /o /h SHA256
if ($LASTEXITCODE) { $log; throw 'makeappx pack failed' }
Write-Host "Packed $msix"

# 5. Reuse or create the dev certificate.
$cert = Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Where-Object { $_.Subject -eq $CertSubject -and $_.NotAfter -gt (Get-Date) -and $_.HasPrivateKey } |
    Sort-Object NotAfter | Select-Object -Last 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $CertSubject `
        -CertStoreLocation Cert:\CurrentUser\My -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(2) `
        -TextExtension @('2.5.29.19={text}')
    Write-Host "Created dev certificate $($cert.Thumbprint) in Cert:\CurrentUser\My"
}

# 6. Sign. No timestamp: a self-signed dev certificate has no use for one, and signing stays offline.
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix
if ($LASTEXITCODE) { throw 'signtool sign failed' }

# 7. App Installer file for this build.
[xml]$appInstaller = Get-Content "$PSScriptRoot\Tomte.appinstaller"
$appInstaller.AppInstaller.Uri = "${AppInstallerBaseUri}$name.appinstaller"
$appInstaller.AppInstaller.Version = $Version
$appInstaller.AppInstaller.MainPackage.Name = $manifest.Package.Identity.Name
$appInstaller.AppInstaller.MainPackage.Publisher = $CertSubject
$appInstaller.AppInstaller.MainPackage.Version = $Version
$appInstaller.AppInstaller.MainPackage.Uri = "${AppInstallerBaseUri}$(Split-Path $msix -Leaf)"
$appInstaller.Save((Join-Path $out "$name.appinstaller"))

if ($NoInstall) {
    Write-Host "Signed $msix (not installed)"
    return
}

if ($Register) {
    # Developer Mode: register the unpacked folder for this user. Gives package identity, skips signature trust.
    Add-AppxPackage -Register (Join-Path $stage 'AppxManifest.xml') -ForceApplicationShutdown
} else {
    # 8. Trust the certificate on this machine, once.
    if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)")) {
        Write-Host 'Adding the dev certificate to LocalMachine\TrustedPeople (UAC prompt).'
        # The public certificate goes in as base64 on the command line, so no file is written.
        $publicCert = [Convert]::ToBase64String($cert.RawData)
        $import = "`$s = New-Object Security.Cryptography.X509Certificates.X509Store TrustedPeople, LocalMachine; " +
            "`$s.Open('ReadWrite'); `$s.Add([Security.Cryptography.X509Certificates.X509Certificate2][Convert]::FromBase64String('$publicCert')); `$s.Close()"
        $failed = 'The certificate was not trusted'
        $retry = "The signed .msix is in $out; rerun and accept the prompt, or use -Register."
        try { Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-Command', $import }
        catch { throw "$failed ($($_.Exception.Message)). $retry" }
        if (-not (Test-Path "Cert:\LocalMachine\TrustedPeople\$($cert.Thumbprint)")) { throw "$failed (import did not land in the store). $retry" }
    }

    # 9. Install.
    Add-AppxPackage -Path $msix -ForceApplicationShutdown
}
$package = Get-AppxPackage -Name $manifest.Package.Identity.Name
Write-Host "Installed $($package.PackageFullName)"
Write-Host "Launch:    explorer.exe shell:AppsFolder\$($package.PackageFamilyName)!$name"
Write-Host "Uninstall: Remove-AppxPackage $($package.PackageFullName)"
