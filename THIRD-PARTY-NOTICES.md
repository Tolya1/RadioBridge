# Third-party notices

RadioBridge is licensed under the Apache License 2.0. It uses the components below, and
self-contained builds include them. Each is distributed under its own license.

| Component | License | Project |
|---|---|---|
| .NET runtime and libraries, Microsoft.Extensions.* | MIT | https://github.com/dotnet/runtime |
| Avalonia | MIT | https://github.com/AvaloniaUI/Avalonia |
| Inter font (via Avalonia.Fonts.Inter) | SIL Open Font License 1.1 | https://github.com/rsms/inter |
| SkiaSharp, HarfBuzzSharp (Avalonia rendering) | MIT | https://github.com/mono/SkiaSharp |
| Skia, HarfBuzz (native libraries inside SkiaSharp) | BSD-3-Clause, MIT | https://skia.org, https://github.com/harfbuzz/harfbuzz |
| ANGLE (`av_libglesv2.dll`, Windows graphics backend) | BSD-3-Clause | https://chromium.googlesource.com/angle/angle |
| HidSharp | Apache-2.0 | https://www.zer7.com/software/hidsharp |
| Serilog, Serilog.Extensions.Logging, Serilog.Sinks.File, Serilog.Sinks.Console | Apache-2.0 | https://github.com/serilog/serilog |

Used only by the tests, not shipped: xUnit (Apache-2.0), Xunit.SkippableFact (MS-PL),
Microsoft.NET.Test.Sdk and Microsoft.Extensions.TimeProvider.Testing (MIT), coverlet (MIT) and
ASP.NET Core (MIT).

**Not included:** Microsoft's `SimConnect.dll`. It belongs to the MSFS SDK and is loaded from the
user's own installation at run time.
