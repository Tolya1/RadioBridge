using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using RadioBridge.UiTests;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace RadioBridge.UiTests;

public sealed class UiTestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    /// <summary>Real text layout (Skia and HarfBuzz) without a screen, so tests can find where text is and click it.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<UiTestApp>()
        .UseSkia()
        .UseHarfBuzz()
        .WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal static class Ui
{
    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(UiTestApp));

    /// <summary>Runs <paramref name="work"/> on the UI thread of the headless Avalonia app.</summary>
    public static Task<T> Run<T>(Func<T> work) => Session.Dispatch(work, CancellationToken.None);

    public static Task Run(Action work) => Session.Dispatch(() => { work(); return true; }, CancellationToken.None);

    public static IEnumerable<T> Descendants<T>(Control root) where T : Control =>
        root.GetVisualDescendants().OfType<T>();
}
