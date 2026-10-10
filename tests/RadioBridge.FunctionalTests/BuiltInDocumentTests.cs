namespace RadioBridge.FunctionalTests;

/// <summary>
/// The documents are built into the executable, so these run the real app (the packaged one under
/// <c>mise run functional:package</c>) and read what it prints.
/// </summary>
[Collection("app")]
public sealed class BuiltInDocumentTests(AppFixture fx)
{
    [Fact]
    public async Task Licenses_prints_the_license_and_the_third_party_notices()
    {
        var (exitCode, output) = await fx.RunToExitAsync("--licenses");

        Assert.Equal(0, exitCode);
        Assert.Contains("Apache License", output);
        Assert.Contains("Version 2.0, January 2004", output);
        Assert.Contains("Third-party notices", output);
        foreach (string component in new[] { "Avalonia", "SkiaSharp", "Serilog", "HidSharp", "Markdig", "Inter font", "ANGLE", ".NET runtime and libraries" })
            Assert.Contains(component, output);
        Assert.Contains("SIL OPEN FONT LICENSE", output);                 // the Inter font's license text, not only its name
        Assert.Contains("Copyright (c) AvaloniaUI OÜ", output);            // non-ASCII survives the console encoding
        Assert.Contains("**Not included:** Microsoft's SimConnect library", output); // the notices are Markdown, printed as written
    }

    [Fact]
    public async Task Readme_prints_the_readme_that_is_built_in()
    {
        var (exitCode, output) = await fx.RunToExitAsync("--readme");

        Assert.Equal(0, exitCode);
        Assert.StartsWith("# RadioBridge", output);
        Assert.Contains("## Using the panel", output);
        Assert.Contains("--licenses", output); // the readme documents its own options
    }

    [Fact]
    public async Task Help_lists_both_options()
    {
        var (exitCode, output) = await fx.RunToExitAsync("--help");

        Assert.Equal(0, exitCode);
        Assert.Contains("--readme", output);
        Assert.Contains("--licenses", output);
    }
}
