using RadioBridge.FakeXPlane;

// Stand-alone X-Plane emulator, for trying RadioBridge without X-Plane:
//   dotnet run --project tests/RadioBridge.FakeXPlane -- [--port 8086] [--com25] [--flip-on-release] [--lag SECONDS] [--refuse] [--api1]
//   --flip-on-release  radios swap only when a held flip command is released (like REP's KX165 in the SF-260)
//   --lag SECONDS      handle every request that much later, in order (like the X-Plane in GitHub issue #1)
//   --refuse           act like X-Plane with "Disable incoming traffic" on (every request gets 403)
//   --api1             act like X-Plane 12.1.1-12.1.3, which has no Web API v2
int port = 8086;
bool com25 = false, refuse = false, api1 = false, flipOnRelease = false;
double lag = 0;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length:
            port = int.Parse(args[++i]);
            break;
        case "--refuse":
            refuse = true;
            break;
        case "--api1":
            api1 = true;
            break;
        case "--com25":
            com25 = true;
            break;
        case "--flip-on-release":
            flipOnRelease = true;
            break;
        case "--lag" when i + 1 < args.Length:
            lag = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
            break;
        default:
            Console.Error.WriteLine("Usage: RadioBridge.FakeXPlane [--port 8086] [--com25] [--flip-on-release] [--lag SECONDS] [--refuse] [--api1]");
            return 2;
    }
}

await using var xplane = new XPlaneEmulator(port)
{
    Com833Capable = !com25,
    RefuseConnections = refuse,
    Api1Only = api1,
    FlipOnRelease = flipOnRelease,
    RequestDelay = TimeSpan.FromSeconds(lag),
};
await xplane.StartAsync();
Console.WriteLine($"Emulated X-Plane Web API on http://127.0.0.1:{xplane.Port}/api/ ({(com25 ? "25 kHz" : "8.33 kHz")} COM radios). Ctrl+C to quit.");

var names = new[]
{
    "sim/cockpit2/radios/actuators/com1_frequency_hz_833",
    "sim/cockpit2/radios/actuators/com1_standby_frequency_hz_833",
    "sim/cockpit2/radios/actuators/nav1_standby_frequency_hz",
    "sim/cockpit2/radios/actuators/transponder_code",
    "sim/cockpit2/gauges/actuators/barometer_setting_in_hg_pilot",
};
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
string last = "";
while (!stop.IsCancellationRequested)
{
    string line = $"COM1 {xplane[names[0]] / 1000:0.000}/{xplane[names[1]] / 1000:0.000}  NAV1 stby {xplane[names[2]] / 100:0.00}  " +
                  $"XPDR {xplane[names[3]]:0000}  ALT {xplane[names[4]]:0.00}  clients {xplane.ConnectedClients}";
    if (line != last)
        Console.WriteLine(line);
    last = line;
    try { await Task.Delay(200, stop.Token); } catch (OperationCanceledException) { }
}
return 0;
