<#
.SYNOPSIS
  Builds THIRD-PARTY-NOTICES.md from the license texts in packaging/notices/sources.
.DESCRIPTION
  The notices file is what ships inside the single-file executable (and is shown by its "Third-party
  notices" button), so it has to contain the real license texts, not just names. This script:
    1. lists the components the app is built from, with their packages and versions (read from the
       restored project, so versions can't drift),
    2. appends the license text of each component, verbatim, from packaging/notices/sources, and
    3. fails if the project depends on a package that no component covers.
  Run it after changing dependencies; `-Check` verifies that the committed file is current (no writes).
  See packaging/notices/sources/README.md for where each source text came from.
#>
param([switch]$Check)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$sources = Join-Path $PSScriptRoot 'sources'
$output = Join-Path $repo 'THIRD-PARTY-NOTICES.md'
$assetsFile = Join-Path $repo 'src\RadioBridge.Desktop\obj\project.assets.json'
if (-not (Test-Path $assetsFile)) { throw 'Restore first: mise run restore.' }

function Read-Source([string]$name) {
    $path = Join-Path $sources $name
    if (-not (Test-Path $path)) { throw "Missing license text: $path" }
    $text = [IO.File]::ReadAllText($path, (New-Object Text.UTF8Encoding($false)))
    $text = $text.TrimStart([char]0xFEFF) -replace "`r`n", "`n" -replace "`r", "`n"
    (($text -split "`n" | ForEach-Object { $_.TrimEnd() }) -join "`n").Trim("`n")
}

# A fence longer than any run of backticks inside the text, so the text can't end the block early.
function Format-Verbatim([string]$text) {
    $longest = ([regex]::Matches($text, '`+') | ForEach-Object { $_.Length } | Measure-Object -Maximum).Maximum
    $fence = '`' * [Math]::Max(3, $longest + 1)
    "$fence" + "text`n$text`n$fence"
}

# Order matters: the first component whose patterns match a package claims it.
$components = @(
    @{ Name = 'ANGLE (Windows graphics backend)';        Packages = @('Avalonia.Angle.Windows.Natives'); License = 'BSD-3-Clause'; Url = 'https://chromium.googlesource.com/angle/angle'; Texts = @('angle-LICENSE.txt') }
    @{ Name = 'Inter font';                              Packages = @('Avalonia.Fonts.Inter');           License = 'SIL OFL 1.1';   Url = 'https://github.com/rsms/inter';                          Texts = @('inter-LICENSE.txt') }
    @{ Name = 'Avalonia';                                Packages = @('Avalonia', 'Avalonia.*');         License = 'MIT';           Url = 'https://github.com/AvaloniaUI/Avalonia';                 Texts = @('avalonia-LICENSE.md') }
    @{ Name = 'MicroCom.Runtime';                        Packages = @('MicroCom.Runtime');               License = 'MIT';           Url = 'https://github.com/kekekeks/MicroCom';                   Texts = @('microcom-LICENSE.txt') }
    @{ Name = 'Tmds.DBus.Protocol';                      Packages = @('Tmds.DBus.Protocol');             License = 'MIT';           Url = 'https://github.com/tmds/Tmds.DBus';                      Texts = @('tmds-dbus-LICENSE.txt') }
    @{ Name = 'SkiaSharp and HarfBuzzSharp';             Packages = @('SkiaSharp', 'SkiaSharp.*', 'HarfBuzzSharp', 'HarfBuzzSharp.*'); License = 'MIT'; Url = 'https://github.com/mono/SkiaSharp'; Texts = @('skiasharp-LICENSE.txt') }
    @{ Name = 'HidSharp';                                Packages = @('HidSharp');                       License = 'Apache-2.0';    Url = 'https://www.zer7.com/software/hidsharp';                 Texts = @('hidsharp-LICENSE.txt') }
    @{ Name = 'Serilog';                                 Packages = @('Serilog', 'Serilog.*');           License = 'Apache-2.0';    Url = 'https://github.com/serilog/serilog';                     Texts = @('serilog-LICENSE.txt') }
    @{ Name = 'Markdig';                                 Packages = @('Markdig');                        License = 'BSD-2-Clause';  Url = 'https://github.com/xoofx/markdig';                       Texts = @('markdig-LICENSE.txt') }
    @{ Name = '.NET runtime and libraries';              Packages = @('Microsoft.Extensions.*');         License = 'MIT';           Url = 'https://github.com/dotnet/runtime';                      Texts = @('dotnet-LICENSE.txt') }
)

# Restored but never part of the shipped executable.
$notShipped = @{
    'AvaloniaUI.DiagnosticsSupport'        = 'developer tools, Debug builds only'
    'Microsoft.IO.RecyclableMemoryStream'  = 'only used by the Debug-only developer tools'
    'Avalonia.BuildServices'               = 'build-time tooling'
}

# --- packages the project restores, grouped by component -------------------------------------
$assets = Get-Content $assetsFile -Raw | ConvertFrom-Json
$packages = $assets.libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } |
    ForEach-Object { $id, $version = $_.Name -split '/', 2; [pscustomobject]@{ Id = $id; Version = $version } } |
    Sort-Object Id

$uncovered = @()
foreach ($package in $packages) {
    if ($notShipped.ContainsKey($package.Id)) { continue }
    $owner = $components | Where-Object { $c = $_; $c.Packages | Where-Object { $package.Id -like $_ } } | Select-Object -First 1
    if ($null -eq $owner) { $uncovered += "$($package.Id) $($package.Version)"; continue }
    if (-not $owner.Used) { $owner.Used = @() }
    $owner.Used += "$($package.Id) $($package.Version)"
}
if ($uncovered) {
    throw "These packages are not covered by any component in generate-third-party-notices.ps1: $($uncovered -join ', '). Add their license text to packaging/notices/sources and a component for them."
}

# --- the document ---------------------------------------------------------------------------
$out = New-Object Text.StringBuilder
function Add([string]$line = '') { [void]$out.Append($line).Append("`n") }

Add '# Third-party notices'
Add
Add 'RadioBridge itself is licensed under the Apache License 2.0 (see the License). Its executable contains the'
Add 'third-party components below, each under its own license. The license texts follow, and so do the notices'
Add 'that ship with the .NET runtime and with the Skia and HarfBuzz native libraries.'
Add
Add "**Not included:** Microsoft's SimConnect library. RadioBridge uses the copy that comes with the user's own"
Add 'Microsoft Flight Simulator installation, and loads it from there.'
Add
Add '## Components'
Add
Add '| Component | License | Project |'
Add '|---|---|---|'
foreach ($c in $components) { Add "| $($c.Name) | $($c.License) | <$($c.Url)> |" }
Add
Add '## License texts'
foreach ($c in $components) {
    Add
    Add "### $($c.Name) ($($c.License))"
    Add
    if ($c.Used) {
        Add "Packages: $((($c.Used) | Sort-Object -Unique) -join ', ')."
        Add
    }
    elseif ($c.Name -like '.NET*') { }
    foreach ($text in $c.Texts) {
        Add (Format-Verbatim (Read-Source $text))
        Add
    }
}
Add '## Notices shipped with the .NET runtime'
Add
Add (Format-Verbatim (Read-Source 'dotnet-THIRD-PARTY-NOTICES.txt'))
Add
Add '## Notices shipped with the Skia and HarfBuzz native libraries'
Add
Add (Format-Verbatim (Read-Source 'skia-THIRD-PARTY-NOTICES.txt'))

$content = ($out.ToString().TrimEnd("`n")) + "`n"

if ($Check) {
    $current = if (Test-Path $output) { ([IO.File]::ReadAllText($output) -replace "`r`n", "`n") } else { '' }
    if ($current -ne $content) {
        Write-Error 'THIRD-PARTY-NOTICES.md is out of date. Run: mise run notices'
        exit 1
    }
    'THIRD-PARTY-NOTICES.md is current.'
    return
}

[IO.File]::WriteAllText($output, $content, (New-Object Text.UTF8Encoding($false)))
"Wrote $output ({0:0} KB, {1} components)" -f ((Get-Item $output).Length / 1KB), $components.Count
