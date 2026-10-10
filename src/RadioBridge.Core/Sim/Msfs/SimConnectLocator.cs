using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace RadioBridge.Core.Sim.Msfs;

/// <summary>What the locator needs to know about the machine. Faked in tests.</summary>
public interface ILocatorEnvironment
{
    string? GetVariable(string name);
    bool FileExists(string path);
    bool DirectoryExists(string path);

    /// <summary>Directory of the application, where a copy may sit next to the executable.</summary>
    string BaseDirectory { get; }

    /// <summary>Whether a Microsoft Flight Simulator process is running.</summary>
    bool IsMsfsRunning { get; }

    /// <summary>Folders of running Microsoft Flight Simulator executables (empty if not readable).</summary>
    IEnumerable<string> RunningMsfsDirectories();

    /// <summary>Steam library folders (the ones that contain a steamapps folder).</summary>
    IEnumerable<string> SteamLibraries();
}

/// <summary>A SimConnect library that was found, and why it was picked.</summary>
public sealed record LocatedSimConnect(string Path, string Source);

/// <summary>
/// Finds a 64-bit SimConnect client library (Windows only). Microsoft hasn't said that SimConnect.dll
/// may be redistributed, so RadioBridge never ships one. Instead it uses a copy that is already on the
/// machine, and the sim's own install folder is the best place: MSFS 2024 carries the library as
/// <c>SimConnect_internal.dll</c> and MSFS 2020 as <c>SimConnect.dll</c>. Search order:
/// <list type="number">
/// <item>the path chosen in the settings (a file or a folder)</item>
/// <item>the <c>SIMCONNECT_DLL</c> variable (a file or a folder)</item>
/// <item>next to RadioBridge itself</item>
/// <item>the folder of a running MSFS, then the Steam installs of MSFS 2024 and 2020</item>
/// <item>the MSFS SDKs</item>
/// </list>
/// </summary>
public static class SimConnectLocator
{
    /// <summary>Library names to look for inside a folder, best first.</summary>
    internal static readonly string[] FileNames = ["SimConnect.dll", "SimConnect_internal.dll"];

    private const string SdkLib = @"SimConnect SDK\lib";

    /// <summary>The first library that exists, or null.</summary>
    public static LocatedSimConnect? Find(string? configuredPath = null, ILocatorEnvironment? environment = null) =>
        FindAll(configuredPath, environment).FirstOrDefault();

    /// <summary>Every library that exists, in search order, so a caller can fall back when one can't be loaded.</summary>
    public static IEnumerable<LocatedSimConnect> FindAll(string? configuredPath = null, ILocatorEnvironment? environment = null)
    {
        environment ??= new MachineEnvironment();
        foreach (var (path, source) in Candidates(configuredPath, environment))
        {
            if (environment.FileExists(path))
                yield return new LocatedSimConnect(path, source);
        }
    }

    /// <summary>Every place searched, in order, without duplicates. Shown in the log when nothing is found.</summary>
    public static IEnumerable<(string Path, string Source)> Candidates(string? configuredPath = null, ILocatorEnvironment? environment = null)
    {
        environment ??= new MachineEnvironment();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in Raw(configuredPath, environment))
        {
            if (seen.Add(candidate.Path))
                yield return candidate;
        }
    }

    private static IEnumerable<(string Path, string Source)> Raw(string? configuredPath, ILocatorEnvironment env)
    {
        // 1. and 2. explicit choices: a file, or a folder holding the library.
        foreach (var (value, source) in new[] { (configuredPath, "the path in the settings"), (env.GetVariable("SIMCONNECT_DLL"), "SIMCONNECT_DLL") })
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;
            if (env.DirectoryExists(value))
            {
                foreach (var name in FileNames)
                    yield return (Path.Combine(value, name), source);
            }
            else
            {
                yield return (value, source);
            }
        }

        // 3. Next to RadioBridge.
        foreach (var name in FileNames)
            yield return (Path.Combine(env.BaseDirectory, name), "next to RadioBridge");

        // 4. The sim's own install folder.
        foreach (var dir in env.RunningMsfsDirectories())
        {
            foreach (var name in FileNames)
                yield return (Path.Combine(dir, name), "the running MSFS");
        }
        foreach (var library in env.SteamLibraries())
        {
            foreach (var game in new[] { "MSFS2024", "MicrosoftFlightSimulator" })
            {
                foreach (var name in FileNames)
                    yield return (Path.Combine(library, "steamapps", "common", game, name), $"the Steam install of {game}");
            }
        }

        // 5. The MSFS SDKs (their installers set these variables).
        foreach (var variable in new[] { "MSFS2024_SDK", "MSFS_SDK" })
        {
            if (env.GetVariable(variable) is { Length: > 0 } sdk)
                yield return (Path.Combine(sdk, SdkLib, "SimConnect.dll"), $"the MSFS SDK ({variable})");
        }
        yield return (Path.Combine(@"C:\MSFS 2024 SDK", SdkLib, "SimConnect.dll"), "the MSFS 2024 SDK");
        yield return (Path.Combine(@"C:\MSFS SDK", SdkLib, "SimConnect.dll"), "the MSFS SDK");
    }
}

/// <summary>The real machine.</summary>
public sealed partial class MachineEnvironment : ILocatorEnvironment
{
    private static readonly string[] MsfsProcessNames = ["FlightSimulator2024", "FlightSimulator"];

    public string? GetVariable(string name) => Environment.GetEnvironmentVariable(name);
    public bool FileExists(string path) => SafeExists(() => File.Exists(path));
    public bool DirectoryExists(string path) => SafeExists(() => Directory.Exists(path));
    public string BaseDirectory => AppContext.BaseDirectory;

    public bool IsMsfsRunning => MsfsProcesses().Any();

    public IEnumerable<string> RunningMsfsDirectories()
    {
        foreach (var process in MsfsProcesses())
        {
            string? directory = null;
            try
            {
                directory = Path.GetDirectoryName(process.MainModule?.FileName);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Not readable (another user's or a protected process): the other search steps still apply.
            }
            finally
            {
                process.Dispose();
            }

            if (!string.IsNullOrEmpty(directory))
                yield return directory;
        }
    }

    public IEnumerable<string> SteamLibraries()
    {
        if (!OperatingSystem.IsWindows())
            yield break;

        string? steam = ReadRegistry(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath")
                        ?? ReadRegistry(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (string.IsNullOrWhiteSpace(steam))
            yield break;

        steam = steam.Replace('/', '\\');
        yield return steam; // the base Steam folder is itself a library

        string text;
        try
        {
            string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                yield break;
            text = File.ReadAllText(vdf);
        }
        catch (IOException)
        {
            yield break;
        }

        foreach (Match match in LibraryPath().Matches(text))
            yield return match.Groups[1].Value.Replace("\\\\", "\\");
    }

    private static IEnumerable<Process> MsfsProcesses() => MsfsProcessNames.SelectMany(Process.GetProcessesByName);

    [GeneratedRegex("\"path\"\\s*\"([^\"]+)\"")]
    private static partial Regex LibraryPath();

    private static bool SafeExists(Func<bool> exists)
    {
        try
        {
            return exists();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistry(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
