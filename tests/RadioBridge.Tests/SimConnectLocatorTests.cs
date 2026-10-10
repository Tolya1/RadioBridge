using RadioBridge.Core.Sim;
using RadioBridge.Core.Sim.Msfs;

namespace RadioBridge.Tests;

/// <summary>A pretend machine: only the listed files and folders exist.</summary>
internal sealed class FakeMachine : ILocatorEnvironment
{
    public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> MsfsFolders { get; } = [];
    public List<string> Steam { get; } = [];
    public bool MsfsRunning { get; set; }

    public string BaseDirectory { get; set; } = SimConnectLocatorTests.P("apps", "RadioBridge");

    public string? GetVariable(string name) => Variables.GetValueOrDefault(name);
    public bool FileExists(string path) => Files.Contains(path);
    public bool DirectoryExists(string path) => Directories.Contains(path);
    public bool IsMsfsRunning => MsfsRunning || MsfsFolders.Count > 0;
    public IEnumerable<string> RunningMsfsDirectories() => MsfsFolders;
    public IEnumerable<string> SteamLibraries() => Steam;
}

/// <summary>
/// The locator is for Windows, but its logic is plain path building, so these tests build every path with
/// <see cref="Path.Combine(string[])"/> exactly like the locator does and run on any operating system.
/// </summary>
public class SimConnectLocatorTests
{
    internal static string P(params string[] parts) => Path.Combine(parts);

    private static string Steam2024(string library) => P(library, "steamapps", "common", "MSFS2024", "SimConnect_internal.dll");

    private static string Sdk(string root) => P(root, @"SimConnect SDK\lib", "SimConnect.dll");

    [Fact]
    public void Finds_the_library_in_the_running_sims_own_folder()
    {
        string folder = P("games", "MSFS2024");
        var machine = new FakeMachine { MsfsFolders = { folder } };
        machine.Files.Add(P(folder, "SimConnect_internal.dll"));

        var found = SimConnectLocator.Find(environment: machine);

        Assert.Equal(new LocatedSimConnect(P(folder, "SimConnect_internal.dll"), "the running MSFS"), found);
    }

    [Fact]
    public void Finds_the_steam_installs_of_both_msfs_versions()
    {
        string main = P("steam"), second = P("library");
        var machine = new FakeMachine { Steam = { main, second } };
        machine.Files.Add(Steam2024(main));
        Assert.Equal(Steam2024(main), SimConnectLocator.Find(environment: machine)?.Path);

        var only2020 = new FakeMachine { Steam = { second } };
        string msfs2020 = P(second, "steamapps", "common", "MicrosoftFlightSimulator", "SimConnect.dll");
        only2020.Files.Add(msfs2020);
        Assert.Equal(msfs2020, SimConnectLocator.Find(environment: only2020)?.Path);
    }

    [Fact]
    public void Prefers_simconnect_dll_over_the_internal_name_in_the_same_folder()
    {
        string folder = P("sim");
        var machine = new FakeMachine { MsfsFolders = { folder } };
        machine.Files.Add(P(folder, "SimConnect_internal.dll"));
        machine.Files.Add(P(folder, "SimConnect.dll"));

        Assert.Equal(P(folder, "SimConnect.dll"), SimConnectLocator.Find(environment: machine)?.Path);
    }

    [Fact]
    public void Search_order_is_settings_then_variable_then_app_then_sim_then_sdk()
    {
        var machine = new FakeMachine { MsfsFolders = { P("sim") }, Steam = { P("steam") } };
        string fromSettings = P("settings", "SimConnect.dll"), fromVariable = P("variable", "SimConnect.dll");
        string sdkRoot = P("sdk");
        machine.Variables["SIMCONNECT_DLL"] = fromVariable;
        machine.Variables["MSFS2024_SDK"] = sdkRoot;
        foreach (var file in new[] { fromSettings, fromVariable, P(machine.BaseDirectory, "SimConnect.dll"), P("sim", "SimConnect_internal.dll"), Sdk(sdkRoot) })
            machine.Files.Add(file);

        var order = SimConnectLocator.FindAll(fromSettings, machine).Select(f => f.Source).ToArray();

        Assert.Equal(
            ["the path in the settings", "SIMCONNECT_DLL", "next to RadioBridge", "the running MSFS", "the MSFS SDK (MSFS2024_SDK)"],
            order);
    }

    [Fact]
    public void A_folder_in_the_settings_or_the_variable_is_searched_for_the_library()
    {
        string libs = P("libs");
        var machine = new FakeMachine();
        machine.Directories.Add(libs);
        machine.Files.Add(P(libs, "SimConnect_internal.dll"));

        Assert.Equal(P(libs, "SimConnect_internal.dll"), SimConnectLocator.Find(libs, machine)?.Path);

        machine.Variables["SIMCONNECT_DLL"] = libs;
        Assert.Equal("SIMCONNECT_DLL", SimConnectLocator.Find(environment: machine)?.Source);
    }

    [Fact]
    public void Nothing_found_returns_null_and_lists_every_place_once()
    {
        string sdkRoot = @"C:\MSFS 2024 SDK";
        var machine = new FakeMachine { MsfsFolders = { P("sim") }, Steam = { P("steam") } };
        machine.Variables["MSFS2024_SDK"] = sdkRoot; // the same folder the built-in list also names

        Assert.Null(SimConnectLocator.Find(environment: machine));

        var places = SimConnectLocator.Candidates(environment: machine).Select(c => c.Path).ToList();
        Assert.Contains(P("sim", "SimConnect_internal.dll"), places);
        Assert.Contains(Steam2024(P("steam")), places);
        Assert.Equal(places.Count, places.Distinct(StringComparer.OrdinalIgnoreCase).Count()); // the SDK folder isn't listed twice
    }

    [Fact]
    public void FindAll_keeps_going_past_a_library_that_exists_so_the_caller_can_fall_back()
    {
        var machine = new FakeMachine { MsfsFolders = { P("sim") } };
        machine.Files.Add(P("sim", "SimConnect_internal.dll"));
        machine.Files.Add(Sdk(@"C:\MSFS SDK"));

        Assert.Equal(2, SimConnectLocator.FindAll(environment: machine).Count());
    }
}

public class MsfsBackendNoticeTests
{
    private static async Task<SimNotice?> NoticeAfterStart(FakeMachine machine, Func<string?>? configured = null)
    {
        using var backend = new MsfsBackend(configured, machine);
        backend.Start();
        await Task.Delay(500); // the first pass of the backend's loop
        return backend.Notice;
    }

    [SkippableFact]
    public async Task Missing_library_with_msfs_running_tells_the_user_and_offers_to_locate_it()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The MSFS backend is Windows-only.");

        var notice = await NoticeAfterStart(new FakeMachine { MsfsRunning = true });

        Assert.NotNull(notice);
        Assert.True(notice.CanLocateSimConnect);
        Assert.Contains("no SimConnect library", notice.Text);
    }

    [SkippableFact]
    public async Task Missing_library_without_msfs_is_not_a_problem()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The MSFS backend is Windows-only.");

        Assert.Null(await NoticeAfterStart(new FakeMachine()));
    }

    [SkippableFact]
    public async Task A_file_that_is_not_a_library_is_reported_as_unusable()
    {
        Skip.IfNot(OperatingSystem.IsWindows(), "The MSFS backend is Windows-only.");
        string bogus = Path.Combine(Path.GetTempPath(), $"not-simconnect-{Guid.NewGuid():N}.dll");
        File.WriteAllText(bogus, "this is not a PE file");
        try
        {
            var machine = new FakeMachine();
            machine.Files.Add(bogus);

            var notice = await NoticeAfterStart(machine, () => bogus);

            Assert.NotNull(notice);
            Assert.True(notice.CanLocateSimConnect);
            Assert.Contains("isn't a usable 64-bit SimConnect library", notice.Text);
        }
        finally
        {
            File.Delete(bogus);
        }
    }
}
