using System.Text;

namespace RadioBridge.Core.Docs;

/// <summary>The documents that are built into the executable.</summary>
public enum DocumentKind
{
    Readme,
    License,
    ThirdPartyNotices,
}

/// <summary>Where a link inside a document leads.</summary>
public abstract record LinkTarget;

/// <summary>A heading in the document the link is in.</summary>
public sealed record AnchorLink(string Slug) : LinkTarget;

/// <summary>One of the built-in documents, optionally at a heading.</summary>
public sealed record DocumentLink(DocumentKind Document, string? Slug) : LinkTarget;

/// <summary>A web page, to open in the browser.</summary>
public sealed record ExternalLink(string Url) : LinkTarget;

public static class DocumentLinks
{
    public const string Repository = "https://github.com/Tolya1/RadioBridge";

    /// <summary>
    /// GitHub's anchor for a heading, which the README's own links use: lower case, only letters, digits,
    /// hyphens and underscores kept, spaces turned into hyphens ("2. Other panel software" becomes
    /// "2-other-panel-software").
    /// </summary>
    public static string Slug(string heading)
    {
        var slug = new StringBuilder(heading.Length);
        foreach (char c in heading.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c is '-' or '_')
                slug.Append(c);
            else if (c == ' ')
                slug.Append('-');
        }
        return slug.ToString();
    }

    /// <summary>
    /// Works out what a link in a document should do, or null for one that is ignored. Only web and mail links
    /// are ever opened outside the app; links to the built-in documents switch to them; any other relative
    /// link is a file in the repository, so it opens on GitHub.
    /// </summary>
    public static LinkTarget? Resolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        url = url.Trim();

        if (url.StartsWith('#'))
            return url.Length > 1 ? new AnchorLink(url[1..]) : null;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return uri.Scheme is "http" or "https" or "mailto" ? new ExternalLink(url) : null;

        string path = url, fragment = "";
        int hash = url.IndexOf('#');
        if (hash >= 0)
            (path, fragment) = (url[..hash], url[(hash + 1)..]);
        path = path.TrimStart('/');
        if (path.Length == 0)
            return null;

        string? slug = fragment.Length > 0 ? fragment : null;
        return path switch
        {
            "README.md" => new DocumentLink(DocumentKind.Readme, slug),
            "LICENSE" => new DocumentLink(DocumentKind.License, slug),
            "THIRD-PARTY-NOTICES.md" => new DocumentLink(DocumentKind.ThirdPartyNotices, slug),
            _ => new ExternalLink($"{Repository}/blob/main/{path}{(slug is null ? "" : "#" + slug)}"),
        };
    }
}

/// <summary>When the readme and notices window opens by itself.</summary>
public static class StartupReader
{
    /// <summary>
    /// Shown on a normal start until the user chooses "Don't show again". Not when RadioBridge was told to
    /// start hidden, and never in an automated run: nobody is there to close it.
    /// </summary>
    public static bool ShouldShow(BridgeOptions options, Settings.BridgeSettings settings, bool startHidden) =>
        settings.ShowReaderAtStart && !startHidden && options.AutomationPort is null;
}
