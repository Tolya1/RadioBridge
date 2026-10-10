# License texts

Verbatim license texts for the components RadioBridge ships, used by
`../generate-third-party-notices.ps1` to build `THIRD-PARTY-NOTICES.md`. They are never edited by hand:
refresh them from their source when a dependency is updated, then run `mise run notices`.

| File | Source |
|---|---|
| `dotnet-LICENSE.txt`, `dotnet-THIRD-PARTY-NOTICES.txt` | `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` in the NuGet runtime pack `microsoft.netcore.app.runtime.win-x64` that the single-file build bundles (`%USERPROFILE%\.nuget\packages`) |
| `skiasharp-LICENSE.txt`, `skia-THIRD-PARTY-NOTICES.txt` | `LICENSE.txt` and `THIRD-PARTY-NOTICES.txt` in the `SkiaSharp.NativeAssets.Win32` package (the `HarfBuzzSharp.NativeAssets.Win32` package carries identical files) |
| `hidsharp-LICENSE.txt` | `LICENSE.txt` in the `HidSharp` package |
| `angle-LICENSE.txt` | `LICENSE` in the `Avalonia.Angle.Windows.Natives` package |
| `avalonia-LICENSE.md` | https://github.com/AvaloniaUI/Avalonia (`licence.md`) |
| `inter-LICENSE.txt` | https://github.com/rsms/inter (`LICENSE.txt`) |
| `markdig-LICENSE.txt` | https://github.com/xoofx/markdig (`license.txt`) |
| `microcom-LICENSE.txt` | https://github.com/kekekeks/MicroCom (`LICENSE`) |
| `tmds-dbus-LICENSE.txt` | https://github.com/tmds/Tmds.DBus (`COPYING`) |
| `serilog-LICENSE.txt` | https://github.com/serilog/serilog (`LICENSE`) |
