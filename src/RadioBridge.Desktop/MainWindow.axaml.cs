using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using RadioBridge.Core.Docs;
using RadioBridge.Desktop.Reader;

namespace RadioBridge.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void OnLocateSimConnect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the 64-bit SimConnect library (SimConnect.dll, or SimConnect_internal.dll in the MSFS 2024 folder)",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("SimConnect library") { Patterns = ["*.dll"] }],
        });
        if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
            viewModel.SetSimConnectPath(path);
    }

    private void OnUseAutomaticSimConnect(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.SetSimConnectPath(null);
    }

    private void OnToggleRecording(object? sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel)
            viewModel.ToggleRecording();
    }

    private async void OnOpenRecordings(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel)
            return;
        var folder = Directory.CreateDirectory(viewModel.RecordingDirectory);
        await Launcher.LaunchDirectoryInfoAsync(folder);
    }

    private ReaderWindow? _reader;

    private void OnOpenReadme(object? sender, RoutedEventArgs e) => OpenReader(DocumentKind.Readme);

    private void OnOpenLicense(object? sender, RoutedEventArgs e) => OpenReader(DocumentKind.License);

    private void OnOpenNotices(object? sender, RoutedEventArgs e) => OpenReader(DocumentKind.ThirdPartyNotices);

    /// <summary>Opens the documents window (or brings the open one to the front, on the document asked for).</summary>
    /// <param name="dontShowAgain">Given when RadioBridge opens it by itself at start; adds the "Don't show again" button.</param>
    internal void OpenReader(DocumentKind kind, Action? dontShowAgain = null)
    {
        if (_reader is { } open)
        {
            open.ShowDocument(kind);
            open.Activate();
            return;
        }

        _reader = new ReaderWindow(kind, dontShowAgain);
        _reader.Closed += (_, _) => _reader = null;
        _reader.Show(this);
    }
}
