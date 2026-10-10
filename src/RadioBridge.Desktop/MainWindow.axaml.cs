using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

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
}
