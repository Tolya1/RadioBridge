using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using RadioBridge.Core.Docs;
using RadioBridge.Desktop.Reader;

namespace RadioBridge.UiTests;

/// <summary>
/// The reader against real text layout: the Markdown renders, the links in it respond to clicks, and the
/// window behaves. Nothing here is a screenshot; it checks what a person would find by looking and clicking.
/// </summary>
public class ReaderTests
{
    private static string Readme => DocumentLibrary.Read(DocumentKind.Readme);

    private static Window Show(Control content, double width = 900, double height = 700)
    {
        var window = new Window { Width = width, Height = height, Content = new ScrollViewer { Content = content } };
        window.Show();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        return window;
    }

    /// <summary>The text of a block built from inline runs (its <c>Text</c> property stays empty then).</summary>
    private static string TextOf(SelectableTextBlock block) => string.Concat(Flatten(block.Inlines));

    private static IEnumerable<string> Flatten(IEnumerable<Avalonia.Controls.Documents.Inline>? inlines) =>
        (inlines ?? []).SelectMany(inline => inline switch
        {
            Avalonia.Controls.Documents.Run run => [run.Text ?? ""],
            Avalonia.Controls.Documents.Span span => Flatten(span.Inlines),
            _ => Array.Empty<string>(),
        });

    /// <summary>The window position of the middle of a character range, for aiming a click.</summary>
    private static Point CenterOf(Window window, SelectableTextBlock block, int start, int length)
    {
        var rects = block.TextLayout.HitTestTextRange(start, length).ToList();
        Assert.NotEmpty(rects);
        var inBlock = rects[0].Center;
        return block.TranslatePoint(inBlock, window) ?? throw new InvalidOperationException("The text is not on screen.");
    }

    private static void Click(Window window, Point point)
    {
        window.MouseMove(point);
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    [Fact]
    public async Task The_readme_renders_with_a_heading_for_every_section_link()
    {
        var anchors = await Ui.Run(() => new MarkdownRenderer(_ => { }).Render(Readme).Anchors);

        foreach (string slug in new[] { "what-works-where", "getting-started", "2-other-panel-software", "using-the-panel", "troubleshooting", "license" })
            Assert.Contains(slug, anchors.Keys);
    }

    [Fact]
    public async Task Clicking_a_contents_link_follows_it_and_clicking_plain_text_does_nothing()
    {
        var followed = new List<LinkTarget>();
        await Ui.Run(() =>
        {
            var rendered = new MarkdownRenderer(followed.Add).Render(Readme);
            var window = Show(rendered.Content);

            var link = Ui.Descendants<SelectableTextBlock>(window).First(b => TextOf(b) == "Getting started");
            Click(window, CenterOf(window, link, 3, 2));

            var paragraph = Ui.Descendants<SelectableTextBlock>(window).First(b => TextOf(b).StartsWith("RadioBridge drives the"));
            Click(window, CenterOf(window, paragraph, 3, 2));
            window.Close();
        });

        Assert.Equal([new AnchorLink("getting-started")], followed);
    }

    [Fact]
    public async Task Every_link_in_the_contents_list_follows_to_its_own_section()
    {
        var followed = new List<LinkTarget>();
        var expected = await Ui.Run(() =>
        {
            var rendered = new MarkdownRenderer(followed.Add).Render(Readme);
            var window = Show(rendered.Content, height: 4000); // tall enough to lay out the whole contents list

            var items = new[] { "What works where", "Getting started", "Using the panel", "Try it without a simulator", "The window and the tray", "Settings", "Command line", "Troubleshooting", "Development", "License" };
            foreach (string item in items)
            {
                var block = Ui.Descendants<SelectableTextBlock>(window).First(b => TextOf(b) == item);
                Click(window, CenterOf(window, block, 0, Math.Min(3, item.Length)));
            }
            window.Close();
            return items.Select(i => (LinkTarget)new AnchorLink(DocumentLinks.Slug(i))).ToList();
        });

        Assert.Equal(expected, followed);
    }

    [Fact]
    public async Task A_web_link_in_the_readme_is_an_external_link()
    {
        var followed = new List<LinkTarget>();
        await Ui.Run(() =>
        {
            var window = Show(new MarkdownRenderer(followed.Add).Render(Readme).Content, height: 4000);
            var paragraph = Ui.Descendants<SelectableTextBlock>(window).First(b => TextOf(b).Contains(".NET 10 SDK"));
            Click(window, CenterOf(window, paragraph, TextOf(paragraph).IndexOf(".NET 10 SDK", StringComparison.Ordinal) + 2, 3));
            window.Close();
        });

        Assert.Equal([new ExternalLink("https://dotnet.microsoft.com/download")], followed);
    }

    [Fact]
    public async Task Selecting_text_by_dragging_across_a_link_does_not_follow_it()
    {
        var followed = new List<LinkTarget>();
        await Ui.Run(() =>
        {
            var window = Show(new MarkdownRenderer(followed.Add).Render(Readme).Content);
            var link = Ui.Descendants<SelectableTextBlock>(window).First(b => TextOf(b) == "Getting started");
            var from = CenterOf(window, link, 0, 1);
            var to = CenterOf(window, link, 12, 1);

            window.MouseMove(from);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton); // the button is held while dragging
            window.MouseUp(to, MouseButton.Left);
            window.Close();
        });

        Assert.Empty(followed);
    }

    [Fact]
    public async Task The_notices_render_their_long_texts_in_pieces_and_a_table_of_components()
    {
        var (longest, tableCells, sections) = await Ui.Run(() =>
        {
            var rendered = new MarkdownRenderer(_ => { }).Render(DocumentLibrary.Read(DocumentKind.ThirdPartyNotices));
            var window = Show(rendered.Content, height: 3000);
            var mono = Ui.Descendants<SelectableTextBlock>(window).Where(b => b.FontSize == 12.5).ToList();
            var result = (
                mono.Max(b => (b.Text ?? "").Split('\n').Length),
                Ui.Descendants<Grid>(window).Sum(g => g.Children.Count),
                rendered.Anchors.Count);
            window.Close();
            return result;
        });

        Assert.True(longest <= 60, $"a piece of a license text has {longest} lines");
        Assert.True(tableCells >= 30, "the component table is missing");
        Assert.True(sections >= 12, "the headings are missing");
    }

    [Fact]
    public async Task The_welcome_window_offers_dont_show_again_and_the_buttons_do_what_they_say()
    {
        bool dismissed = false;
        var (visible, closedAfterDismiss) = await Ui.Run(() =>
        {
            var window = new ReaderWindow(DocumentKind.Readme, () => dismissed = true);
            window.Show();
            var button = window.FindControl<Button>("DontShowAgain")!;
            bool shown = button.IsVisible;

            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return (shown, !window.IsVisible);
        });

        Assert.True(visible);
        Assert.True(dismissed);
        Assert.True(closedAfterDismiss);
    }

    [Fact]
    public async Task Opened_from_a_button_the_window_has_no_dont_show_again_and_opens_on_the_chosen_document()
    {
        var (visible, selected) = await Ui.Run(() =>
        {
            var window = new ReaderWindow(DocumentKind.ThirdPartyNotices);
            window.Show();
            var result = (window.FindControl<Button>("DontShowAgain")!.IsVisible, ((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Tag);
            window.Close();
            return result;
        });

        Assert.False(visible);
        Assert.Equal(DocumentKind.ThirdPartyNotices, selected);
    }

    [Fact]
    public async Task Following_a_link_to_another_document_switches_tab()
    {
        var selected = await Ui.Run(() =>
        {
            var window = new ReaderWindow(DocumentKind.Readme);
            window.Show();
            window.ShowDocument(DocumentKind.License);
            var tag = ((TabItem)window.FindControl<TabControl>("Tabs")!.SelectedItem!).Tag;
            window.Close();
            return tag;
        });

        Assert.Equal(DocumentKind.License, selected);
    }
}
