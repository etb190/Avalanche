param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string]$Tag,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$version = $Tag.Substring(1)
$headers = @{
    Accept = 'application/vnd.github+json'
    'User-Agent' = 'Avalanche-WinGet-Release'
    'X-GitHub-Api-Version' = '2022-11-28'
}
if ($env:GITHUB_TOKEN) { $headers.Authorization = "Bearer $env:GITHUB_TOKEN" }
$release = Invoke-RestMethod "https://api.github.com/repos/etb190/Avalanche/releases/tags/$Tag" -Headers $headers
if ($release.draft -or $release.prerelease -or $release.tag_name -ne $Tag) {
    throw 'WinGet requires a published stable release matching the requested tag.'
}
$assets = @($release.assets | Where-Object { $_.name -eq 'Avalanche.exe' })
if ($assets.Count -ne 1) { throw 'The release must contain exactly one Avalanche.exe installer.' }
$url = "https://github.com/etb190/Avalanche/releases/download/$Tag/Avalanche.exe"
if ($assets[0].browser_download_url -ne $url) { throw 'Unexpected installer download URL.' }

$directory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $directory) { throw 'Use a new output directory to avoid submitting stale manifests.' }
[IO.Directory]::CreateDirectory($directory) | Out-Null
$installerPath = "$directory.exe"
if (Test-Path -LiteralPath $installerPath) { throw 'The installer download path already exists.' }
Invoke-WebRequest -Uri $url -OutFile $installerPath -UseBasicParsing
$hash = (Get-FileHash -LiteralPath $installerPath -Algorithm SHA256).Hash
if ($assets[0].digest -and $assets[0].digest -ne "sha256:$($hash.ToLowerInvariant())") {
    throw 'Downloaded installer does not match the GitHub release digest.'
}
$date = ([datetime]$release.published_at).ToUniversalTime().ToString('yyyy-MM-dd')
$utf8 = New-Object System.Text.UTF8Encoding($false)

# The launcher installs machine-wide with /silent. Its interactive wizard can
# select a user install, so this machine-scoped manifest advertises silent modes only.
$installer = @"
# Created by the Avalanche release workflow
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.12.0.schema.json

PackageIdentifier: etb190.Avalanche
PackageVersion: $version
InstallerType: exe
Scope: machine
InstallModes:
- silent
- silentWithProgress
InstallerSwitches:
  Silent: /silent
  SilentWithProgress: /silent
UpgradeBehavior: install
Dependencies:
  PackageDependencies:
  - PackageIdentifier: Microsoft.DotNet.DesktopRuntime.10
    MinimumVersion: 10.0.0
AppsAndFeaturesEntries:
- DisplayName: Avalanche
  Publisher: Avalanche Team
  DisplayVersion: $version
  ProductCode: Avalanche
ReleaseDate: $date
Installers:
- Architecture: x64
  InstallerUrl: $url
  InstallerSha256: $hash
ManifestType: installer
ManifestVersion: 1.12.0
"@
$releaseNotes = ([string]$release.body).Trim()
if ([string]::IsNullOrWhiteSpace($releaseNotes) -or $releaseNotes.Length -gt 10000) {
    throw 'WinGet requires release notes between 1 and 10000 characters.'
}
$releaseNotes = (($releaseNotes -split '\r?\n') | ForEach-Object { "  $_" }) -join "`r`n"
$locale = @"
# Created by the Avalanche release workflow
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.12.0.schema.json

PackageIdentifier: etb190.Avalanche
PackageVersion: $version
PackageLocale: en-US
Publisher: Avalanche Team
PublisherUrl: https://github.com/etb190
PublisherSupportUrl: https://github.com/etb190/Avalanche/issues
Author: Avalanche Team
PackageName: Avalanche
PackageUrl: https://github.com/etb190/Avalanche
License: GPL-3.0
LicenseUrl: https://github.com/etb190/Avalanche/blob/HEAD/LICENSE
Copyright: Copyright (c) 2026 Avalanche Team
ShortDescription: PDF editor for Windows. No account, no subscription, no telemetry.
Description: Avalanche is a lightweight PDF viewer and toolkit for Windows. View, merge, split, and manage PDF files. Runs portable or installs to your user profile without admin rights. No account, no subscription, no telemetry. Open source under GPLv3.
Moniker: avalanche
Tags:
- dotnet
- gplv3
- opensource
- pdf
- pdf-editor
- portable
- windows
- wpf
ReleaseNotes: |-
$releaseNotes
ReleaseNotesUrl: https://github.com/etb190/Avalanche/releases/tag/$Tag
ManifestType: defaultLocale
ManifestVersion: 1.12.0
"@
$manifest = @"
# Created by the Avalanche release workflow
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.12.0.schema.json

PackageIdentifier: etb190.Avalanche
PackageVersion: $version
DefaultLocale: en-US
ManifestType: version
ManifestVersion: 1.12.0
"@
[IO.File]::WriteAllText((Join-Path $directory 'etb190.Avalanche.installer.yaml'), ($installer -replace '\r?\n', "`r`n"), $utf8)
[IO.File]::WriteAllText((Join-Path $directory 'etb190.Avalanche.locale.en-US.yaml'), ($locale -replace '\r?\n', "`r`n"), $utf8)
[IO.File]::WriteAllText((Join-Path $directory 'etb190.Avalanche.yaml'), ($manifest -replace '\r?\n', "`r`n"), $utf8)
Write-Host "Generated WinGet manifests for $Tag with SHA256 $hash in $directory"
