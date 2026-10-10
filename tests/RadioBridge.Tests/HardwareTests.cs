using HidSharp;
using RadioBridge.Core.Panel;

namespace RadioBridge.Tests;

/// <summary>
/// Checks against a real radio panel: shared access and reading the current state. Opt-in, because
/// they need the hardware and briefly blank its displays: set RADIOBRIDGE_HARDWARE=1
/// (<c>mise run test:hardware</c>). Other panel software and a running RadioBridge may stay open;
/// that's part of what is tested.
/// </summary>
public sealed class HardwareTests
{
    private static HidDevice? Panel() =>
        DeviceList.Local.GetHidDevices(RadioPanelProtocol.VendorId, RadioPanelProtocol.ProductId).FirstOrDefault();

    private static void SkipUnlessHardware()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RADIOBRIDGE_HARDWARE") == "1", "Set RADIOBRIDGE_HARDWARE=1 to run hardware tests.");
        Skip.If(Panel() is null, "No radio panel connected.");
    }

    private static HidRadioPanel StartPanel(List<PanelEvent> events, ManualResetEventSlim connected)
    {
        var panel = new HidRadioPanel();
        panel.Input += e => { lock (events) events.Add(e); };
        panel.ConnectionChanged += c => { if (c) connected.Set(); };
        panel.Start();
        Assert.True(connected.Wait(TimeSpan.FromSeconds(10)), "RadioBridge could not open the panel.");
        return panel;
    }

    [SkippableFact]
    public void Other_programs_can_open_the_panel_while_RadioBridge_has_it()
    {
        SkipUnlessHardware();
        using var connected = new ManualResetEventSlim();
        using var panel = StartPanel([], connected);

        // What another program does (default, non-exclusive access): it must get in and read.
        Assert.True(Panel()!.TryOpen(out HidStream other), "A second program was locked out.");
        using (other)
        {
            var report = new byte[Panel()!.GetMaxFeatureReportLength()];
            other.GetFeature(report);
            Assert.True(CurrentInputReport.Plausible(report));
        }

        panel.Show(new PanelDisplays("", "", "", ""));
        Assert.True(panel.IsConnected);
    }

    [SkippableFact]
    public void RadioBridge_gets_in_while_another_program_has_the_panel()
    {
        SkipUnlessHardware();
        Assert.True(Panel()!.TryOpen(out HidStream other));
        using (other)
        {
            using var connected = new ManualResetEventSlim();
            using var panel = StartPanel([], connected);
            Assert.True(panel.IsConnected);
        }
    }

    [SkippableFact]
    public void Selector_positions_are_known_right_after_connecting()
    {
        SkipUnlessHardware();
        var events = new List<PanelEvent>();
        using var connected = new ManualResetEventSlim();
        using var panel = StartPanel(events, connected);
        Thread.Sleep(300);

        lock (events)
            Assert.Contains(events, e => e is SelectorMoved);
    }
}
