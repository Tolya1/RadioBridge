namespace RadioBridge.Core.Sim.Msfs;

/// <summary>Finds a 64-bit SimConnect.dll on this machine (Windows only).</summary>
public static class SimConnectLocator
{
    private const string FileName = "SimConnect.dll";
    private const string SdkLib = @"SimConnect SDK\lib";

    public static string? Find() => Candidates().FirstOrDefault(File.Exists);

    /// <summary>Places searched, in order. Shown in the README and the log when nothing is found.</summary>
    public static IEnumerable<string> Candidates()
    {
        // 1. Explicit override: a file or a folder.
        if (Environment.GetEnvironmentVariable("SIMCONNECT_DLL") is { Length: > 0 } custom)
        {
            yield return custom;
            yield return Path.Combine(custom, FileName);
        }

        // 2. Next to the app.
        yield return Path.Combine(AppContext.BaseDirectory, FileName);

        // 3. The MSFS SDKs (the installers set these variables).
        foreach (var variable in new[] { "MSFS2024_SDK", "MSFS_SDK" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } sdk)
                yield return Path.Combine(sdk, SdkLib, FileName);
        }
        yield return Path.Combine(@"C:\MSFS 2024 SDK", SdkLib, FileName);
        yield return Path.Combine(@"C:\MSFS SDK", SdkLib, FileName);
    }
}
