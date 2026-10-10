using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RadioBridge.Core.Docs;

namespace RadioBridge.Desktop.Reader;

/// <summary>
/// The built-in documents in tabs. Opened by the buttons in the main window and, until "Don't show again" is
/// chosen, by itself when RadioBridge starts. A tab is only built when it is first shown.
/// </summary>
public partial class ReaderWindow : Window
{
    private static readonly DocumentKind[] Order = [DocumentKind.Readme, DocumentKind.License, DocumentKind.ThirdPartyNotices];

    private readonly Dictionary<DocumentKind, Page> _pages = new();
    private readonly Action? _dontShowAgain;

    /// <summary>For the XAML designer and runtime loader.</summary>
    public ReaderWindow() : this(DocumentKind.Readme)
    {
    }

    /// <param name="dontShowAgain">Set when the window opened by itself: adds the "Don't show again" button.</param>
    public ReaderWindow(DocumentKind initial, Action? dontShowAgain = null)
    {
        InitializeComponent();
        _dontShowAgain = dontShowAgain;

        foreach (var kind in Order)
        {
            var scroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            var tab = new TabItem { Header = DocumentLibrary.Title(kind), Content = scroll, Tag = kind };
            _pages[kind] = new Page(tab, scroll);
            Tabs.Items.Add(tab);
        }
        Tabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == Tabs && Tabs.SelectedItem is TabItem { Tag: DocumentKind kind })
                EnsureRendered(kind);
        };

        if (dontShowAgain is not null)
        {
            Title = "Welcome to RadioBridge";
            DontShowAgain.IsVisible = true;
            Hint.Text = "These are also under the Settings box in the main window.";
        }
        else
        {
            Title = $"RadioBridge: {DocumentLibrary.Title(initial)}";
        }
        ShowDocument(initial);
    }

    /// <summary>Brings a document to the front, optionally scrolled to a heading.</summary>
    public void ShowDocument(DocumentKind kind, string? slug = null)
    {
        Tabs.SelectedItem = _pages[kind].Tab;
        EnsureRendered(kind);
        if (slug is not null)
            ScrollTo(kind, slug);
    }

    private void EnsureRendered(DocumentKind kind)
    {
        var page = _pages[kind];
        if (page.Rendered is not null)
            return;

        string text = DocumentLibrary.Read(kind);
        if (kind == DocumentKind.License)
        {
            // Legal text, not Markdown: show it exactly as written.
            var plain = new SelectableTextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, Menlo, monospace"),
                FontSize = 12.5,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Color.Parse("#d7dae0")),
                Margin = new Thickness(26, 18),
            };
            page.Rendered = new RenderedDocument(plain, new Dictionary<string, Control>());
        }
        else
        {
            page.Rendered = new MarkdownRenderer(target => OnNavigate(kind, target)).Render(text);
        }
        page.Scroll.Content = page.Rendered.Content;
    }

    private void OnNavigate(DocumentKind current, LinkTarget target)
    {
        switch (target)
        {
            case AnchorLink anchor:
                ScrollTo(current, anchor.Slug);
                break;
            case DocumentLink document:
                ShowDocument(document.Document, document.Slug);
                break;
            case ExternalLink external when Uri.TryCreate(external.Url, UriKind.Absolute, out var uri):
                _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
                break;
        }
    }

    private void ScrollTo(DocumentKind kind, string slug)
    {
        if (_pages[kind].Rendered?.Anchors.TryGetValue(slug, out var heading) != true)
            return;
        // The tab may only just have been laid out.
        Dispatcher.UIThread.Post(() => heading!.BringIntoView(), DispatcherPriority.Loaded);
    }

    private void OnDontShowAgain(object? sender, RoutedEventArgs e)
    {
        _dontShowAgain?.Invoke();
        Close();
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private sealed class Page(TabItem tab, ScrollViewer scroll)
    {
        public TabItem Tab { get; } = tab;
        public ScrollViewer Scroll { get; } = scroll;
        public RenderedDocument? Rendered { get; set; }
    }
}
