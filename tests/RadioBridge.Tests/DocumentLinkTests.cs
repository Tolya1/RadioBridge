using RadioBridge.Core;
using RadioBridge.Core.Docs;
using RadioBridge.Core.Settings;

namespace RadioBridge.Tests;

public class DocumentLinkTests
{
    [Theory]
    [InlineData("What works where", "what-works-where")]
    [InlineData("Getting started", "getting-started")]
    [InlineData("2. Other panel software", "2-other-panel-software")]
    [InlineData("Try it without a simulator", "try-it-without-a-simulator")]
    [InlineData("The window and the tray", "the-window-and-the-tray")]
    [InlineData("COM1 / COM2", "com1--com2")]
    [InlineData("Troubleshooting", "troubleshooting")]
    [InlineData("`mise run ci`", "mise-run-ci")]
    public void Slugs_match_githubs_anchors_so_the_readmes_own_links_work(string heading, string slug) =>
        Assert.Equal(slug, DocumentLinks.Slug(heading));

    [Fact]
    public void Every_internal_link_in_the_readme_points_at_a_heading_that_exists()
    {
        string readme = File.ReadAllText(Path.Combine(RepositoryRoot(), "README.md"));
        var headings = readme.Split('\n')
            .Where(line => line.StartsWith('#'))
            .Select(line => DocumentLinks.Slug(line.TrimStart('#').Trim()))
            .ToHashSet();

        var anchors = System.Text.RegularExpressions.Regex.Matches(readme, @"\]\(#([^)]+)\)").Select(m => m.Groups[1].Value).ToList();

        Assert.NotEmpty(anchors);
        Assert.All(anchors, anchor => Assert.Contains(anchor, headings));
    }

    [Fact]
    public void Web_links_open_in_the_browser_and_unsafe_schemes_are_ignored()
    {
        Assert.Equal(new ExternalLink("https://dotnet.microsoft.com/download"), DocumentLinks.Resolve("https://dotnet.microsoft.com/download"));
        Assert.Equal(new ExternalLink("mailto:someone@example.com"), DocumentLinks.Resolve("mailto:someone@example.com"));
        Assert.Null(DocumentLinks.Resolve("file:///C:/Windows/System32/cmd.exe"));
        Assert.Null(DocumentLinks.Resolve("javascript:alert(1)"));
        Assert.Null(DocumentLinks.Resolve(""));
        Assert.Null(DocumentLinks.Resolve(null));
        Assert.Null(DocumentLinks.Resolve("#"));
    }

    [Fact]
    public void Links_to_the_built_in_documents_switch_to_them()
    {
        Assert.Equal(new AnchorLink("troubleshooting"), DocumentLinks.Resolve("#troubleshooting"));
        Assert.Equal(new DocumentLink(DocumentKind.License, null), DocumentLinks.Resolve("LICENSE"));
        Assert.Equal(new DocumentLink(DocumentKind.ThirdPartyNotices, null), DocumentLinks.Resolve("THIRD-PARTY-NOTICES.md"));
        Assert.Equal(new DocumentLink(DocumentKind.Readme, "using-the-panel"), DocumentLinks.Resolve("README.md#using-the-panel"));
    }

    [Fact]
    public void Other_repository_files_open_on_github()
    {
        Assert.Equal(new ExternalLink($"{DocumentLinks.Repository}/blob/main/docs/architecture.md"), DocumentLinks.Resolve("docs/architecture.md"));
        Assert.Equal(new ExternalLink($"{DocumentLinks.Repository}/blob/main/docs/testing.md#automation-api"), DocumentLinks.Resolve("docs/testing.md#automation-api"));
    }

    [Theory]
    [InlineData(true, false, null, true)]    // a normal start: show it
    [InlineData(false, false, null, false)]  // "Don't show again" was chosen
    [InlineData(true, true, null, false)]    // started hidden or minimized
    [InlineData(true, false, 47800, false)]  // an automated run
    public void The_reader_opens_by_itself_only_on_a_normal_start(bool setting, bool startHidden, int? automationPort, bool expected)
    {
        var options = new BridgeOptions { AutomationPort = automationPort };
        var settings = new BridgeSettings { ShowReaderAtStart = setting };

        Assert.Equal(expected, StartupReader.ShouldShow(options, settings, startHidden));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RadioBridge.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }
}
