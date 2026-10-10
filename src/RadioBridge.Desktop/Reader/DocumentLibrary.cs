using System.Reflection;
using RadioBridge.Core.Docs;

namespace RadioBridge.Desktop.Reader;

/// <summary>The readme and license texts that are built into the executable.</summary>
internal static class DocumentLibrary
{
    public static string FileName(DocumentKind kind) => kind switch
    {
        DocumentKind.Readme => "README.md",
        DocumentKind.License => "LICENSE",
        _ => "THIRD-PARTY-NOTICES.md",
    };

    public static string Title(DocumentKind kind) => kind switch
    {
        DocumentKind.Readme => "Readme",
        DocumentKind.License => "License",
        _ => "Third-party notices",
    };

    public static string Read(DocumentKind kind)
    {
        string name = FileName(kind);
        using var stream = typeof(DocumentLibrary).Assembly.GetManifestResourceStream(name)
                           ?? throw new InvalidOperationException($"{name} is not built into this executable.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}

internal static class AppInfo
{
    /// <summary>"0.1.0 (befe19c)": the version and the commit it was built from, for bug reports.</summary>
    public static string VersionText
    {
        get
        {
            var assembly = typeof(AppInfo).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                             ?? assembly.GetName().Version?.ToString(3) ?? "?";
            int plus = version.IndexOf('+');
            return plus < 0 ? version : $"{version[..plus]} ({version[(plus + 1)..][..Math.Min(7, version.Length - plus - 1)]})";
        }
    }
}
