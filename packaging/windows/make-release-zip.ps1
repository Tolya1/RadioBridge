<#
.SYNOPSIS
  Builds the Windows release package: dist\RadioBridge-<version>-win-x64.zip.
.DESCRIPTION
  Publishes the single-file, self-contained win-x64 executable from a clean checkout and zips it. The zip
  holds just the exe: the readme, license and third-party notices are built into it. Checks that the
  notices are current first, and that the packaged exe really carries its license texts. Prints the
  SHA-256 of the zip.
  The version comes from Directory.Build.props. Run it from anywhere: `mise run package:windows`.
#>
param(
    [string]$Output = 'dist'
)
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Set-Location $repo

if (git status --porcelain) {
    throw 'The working tree has uncommitted changes; commit them first so the package matches a commit.'
}
$version = ([xml](Get-Content Directory.Build.props)).Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
$commit = git rev-parse --short HEAD

& (Join-Path $repo 'packaging\notices\generate-third-party-notices.ps1') -Check
if ($LASTEXITCODE -ne 0) { throw 'THIRD-PARTY-NOTICES.md is out of date: run mise run notices and commit.' }

$stage = Join-Path $Output 'stage-win-x64'
$name = "RadioBridge-$version-win-x64"
$zip = Join-Path $Output "$name.zip"
Remove-Item -Recurse -Force $stage, $zip -ErrorAction SilentlyContinue

dotnet publish src/RadioBridge.Desktop -c Release -r win-x64 -p:SingleFile=true -o $stage
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# A single-file package is the executable and nothing else: the readme, the license and the third-party
# notices are built into it (its buttons show them, --licenses prints them).
$extra = Get-ChildItem $stage -File | Where-Object Name -ne 'RadioBridge.exe'
if ($extra) { throw "Unexpected files next to the executable: $($extra.Name -join ', ')" }

# Prove it before shipping: the exe has to carry the license texts itself.
$printed = & (Join-Path $stage 'RadioBridge.exe') --licenses | Out-String
foreach ($needle in 'Apache License', 'Third-party notices', 'SIL OPEN FONT LICENSE', 'SkiaSharp') {
    if (-not $printed.Contains($needle)) { throw "The packaged exe doesn't carry its license texts (missing '$needle')." }
}

Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal
Remove-Item -Recurse -Force $stage

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"{0}  ({1:0.0} MB, commit {2})" -f (Resolve-Path $zip), ((Get-Item $zip).Length / 1MB), $commit
"SHA-256: $hash"
