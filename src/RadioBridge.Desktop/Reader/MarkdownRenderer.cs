using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using RadioBridge.Core.Docs;

namespace RadioBridge.Desktop.Reader;

/// <summary>A document turned into controls, and the headings that links can jump to.</summary>
internal sealed record RenderedDocument(Control Content, IReadOnlyDictionary<string, Control> Anchors);

/// <summary>
/// Shows Markdown with ordinary Avalonia controls (Avalonia has no Markdown view of its own, and the available
/// ones don't support Avalonia 12 or need a commercial license). Covers what the readme and notices use:
/// headings, paragraphs, bold, italic, code, links, lists, quotes, tables and fenced code. Images are left out.
/// One instance renders one document.
/// </summary>
internal sealed class MarkdownRenderer(Action<LinkTarget> navigate)
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().UseAutoLinks().Build();

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, DejaVu Sans Mono, Menlo, monospace");
    private static readonly IBrush Body = new SolidColorBrush(Color.Parse("#d7dae0"));
    private static readonly IBrush Heading = new SolidColorBrush(Color.Parse("#f0f2f5"));
    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#9aa0aa"));
    private static readonly IBrush Link = new SolidColorBrush(Color.Parse("#6cb6ff"));
    private static readonly IBrush CodeBackground = new SolidColorBrush(Color.Parse("#0d0e10"));
    private static readonly IBrush InlineCodeBackground = new SolidColorBrush(Color.Parse("#23262b"));
    private static readonly IBrush Line = new SolidColorBrush(Color.Parse("#2a2d33"));

    // Long code blocks (the license notices run to thousands of lines) are laid out in pieces: one giant
    // text block makes the first display of the tab slow.
    private const int LinesPerPiece = 60;

    private readonly Dictionary<string, Control> _anchors = new();
    private readonly Dictionary<string, int> _headingsSeen = new();

    public RenderedDocument Render(string markdown)
    {
        var document = Markdown.Parse(markdown, Pipeline);
        var root = new StackPanel { Spacing = 10, Margin = new Thickness(26, 18, 26, 26) };
        foreach (var block in document)
        {
            if (Block(block, depth: 0, bold: false) is { } control)
                root.Children.Add(control);
        }
        return new RenderedDocument(root, _anchors);
    }

    // ---- blocks ----

    private Control? Block(Block block, int depth, bool bold) => block switch
    {
        HeadingBlock heading => HeadingOf(heading),
        ParagraphBlock paragraph => Paragraph(paragraph, bold),
        ListBlock list => ListOf(list, depth),
        FencedCodeBlock or CodeBlock => Code((LeafBlock)block),
        QuoteBlock quote => QuoteOf(quote, depth),
        Table table => TableOf(table),
        ThematicBreakBlock => new Border { Height = 1, Background = Line, Margin = new Thickness(0, 8) },
        _ => null, // HTML, link definitions, ...
    };

    /// <summary>A paragraph, or nothing for one that only held an image (which isn't shown, so it must not leave a gap).</summary>
    private Control? Paragraph(ParagraphBlock paragraph, bool bold)
    {
        var text = Text(paragraph.Inline, 14, bold ? FontWeight.Bold : FontWeight.Normal);
        return text.Inlines is { Count: 0 } ? null : text;
    }

    private Control HeadingOf(HeadingBlock heading)
    {
        double size = heading.Level switch { 1 => 28, 2 => 22, 3 => 18, 4 => 16, _ => 14 };
        var text = Text(heading.Inline, size, FontWeight.Bold, Heading);
        text.Margin = new Thickness(0, heading.Level <= 2 ? 14 : 8, 0, 0);

        string slug = DocumentLinks.Slug(Plain(heading.Inline));
        if (slug.Length > 0)
        {
            // GitHub numbers repeated headings: "usage", "usage-1", ...
            int seen = _headingsSeen.GetValueOrDefault(slug);
            _headingsSeen[slug] = seen + 1;
            _anchors.TryAdd(seen == 0 ? slug : $"{slug}-{seen}", text);
        }
        return text;
    }

    private Control ListOf(ListBlock list, int depth)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(depth > 0 ? 6 : 0, 0, 0, 0) };
        int number = int.TryParse(list.OrderedStart, out int start) ? start : 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            var content = new StackPanel { Spacing = 4 };
            foreach (var child in item)
            {
                if (Block(child, depth + 1, bold: false) is { } control)
                    content.Children.Add(control);
            }

            var marker = new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", Foreground = Muted, FontSize = 14, MinWidth = 18 };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
            Grid.SetColumn(content, 1);
            row.Children.Add(marker);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    private static Control Code(LeafBlock block)
    {
        var lines = Enumerable.Range(0, block.Lines.Count).Select(i => block.Lines.Lines[i].Slice.ToString()).ToList();
        var pieces = new StackPanel();
        for (int i = 0; i < lines.Count || i == 0; i += LinesPerPiece)
        {
            pieces.Children.Add(new SelectableTextBlock
            {
                Text = string.Join("\n", lines.Skip(i).Take(LinesPerPiece)),
                FontFamily = Mono,
                FontSize = 12.5,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Body,
            });
            if (lines.Count == 0)
                break;
        }
        return new Border
        {
            Background = CodeBackground,
            BorderBrush = Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            Child = pieces,
        };
    }

    private Control QuoteOf(QuoteBlock quote, int depth)
    {
        var content = new StackPanel { Spacing = 6 };
        foreach (var child in quote)
        {
            if (Block(child, depth + 1, bold: false) is { } control)
                content.Children.Add(control);
        }
        return new Border { BorderBrush = Line, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2, 0, 2), Child = content };
    }

    private Control TableOf(Table table)
    {
        var rows = table.OfType<TableRow>().ToList();
        int columns = rows.Max(row => row.Count);

        // Wider text gets more room, but no column is squeezed to nothing or allowed to take over.
        var weights = Enumerable.Range(0, columns)
            .Select(c => Math.Clamp(rows.Max(row => c < row.Count ? Plain((TableCell)row[c]).Length : 0), 6, 60))
            .ToList();

        var grid = new Grid();
        foreach (int weight in weights)
            grid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(weight, GridUnitType.Star)) { MinWidth = 48 });

        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (int c = 0; c < rows[r].Count; c++)
            {
                var cell = (TableCell)rows[r][c];
                var content = new StackPanel { Spacing = 2 };
                foreach (var block in cell)
                {
                    if (Block(block, 1, bold: rows[r].IsHeader) is { } control)
                        content.Children.Add(control);
                }

                var border = new Border
                {
                    BorderBrush = Line,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 6, 12, 6),
                    Child = content,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return grid;
    }

    // ---- text ----

    private SelectableTextBlock Text(ContainerInline? inlines, double size, FontWeight weight, IBrush? foreground = null)
    {
        var block = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = size,
            FontWeight = weight,
            Foreground = foreground ?? Body,
            LineHeight = size * 1.5,
        };

        var links = new List<(int Start, int End, LinkTarget Target)>();
        int position = 0;
        if (inlines is not null)
            AddInlines(block.Inlines!, inlines, links, ref position);
        if (links.Count > 0)
            MakeClickable(block, links);
        return block;
    }

    private static void AddInlines(InlineCollection target, ContainerInline container, List<(int, int, LinkTarget)> links, ref int position)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    AddRun(target, literal.Content.ToString(), ref position);
                    break;

                case HtmlEntityInline entity:
                    AddRun(target, entity.Transcoded.ToString(), ref position);
                    break;

                case CodeInline code:
                    var run = new Run(code.Content) { FontFamily = Mono, Background = InlineCodeBackground };
                    target.Add(run);
                    position += code.Content.Length;
                    break;

                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterCount >= 2)
                        span.FontWeight = FontWeight.Bold;
                    else
                        span.FontStyle = FontStyle.Italic;
                    AddInlines(span.Inlines, emphasis, links, ref position);
                    target.Add(span);
                    break;

                case LinkInline { IsImage: true }:
                    break; // the window itself is the picture

                case LinkInline link:
                    var linkTarget = DocumentLinks.Resolve(link.Url);
                    var linkSpan = new Span();
                    int start = position;
                    AddInlines(linkSpan.Inlines, link, links, ref position);
                    if (linkTarget is not null)
                    {
                        linkSpan.Foreground = Link;
                        linkSpan.TextDecorations = TextDecorations.Underline;
                        links.Add((start, position, linkTarget));
                    }
                    target.Add(linkSpan);
                    break;

                case AutolinkInline auto:
                    var autoTarget = DocumentLinks.Resolve(auto.Url);
                    int autoStart = position;
                    var autoRun = new Run(auto.Url);
                    position += auto.Url.Length;
                    if (autoTarget is not null)
                    {
                        autoRun.Foreground = Link;
                        autoRun.TextDecorations = TextDecorations.Underline;
                        links.Add((autoStart, position, autoTarget));
                    }
                    target.Add(autoRun);
                    break;

                case LineBreakInline { IsHard: true }:
                    AddRun(target, "\n", ref position); // a run, not a LineBreak inline: exactly one character, so link positions stay exact
                    break;

                case LineBreakInline:
                    AddRun(target, " ", ref position);
                    break;

                case ContainerInline nested:
                    AddInlines(target, nested, links, ref position);
                    break;
            }
        }
    }

    private static void AddRun(InlineCollection target, string text, ref int position)
    {
        target.Add(new Run(text));
        position += text.Length;
    }

    /// <summary>Clicking a link in a text block opens it. Inline runs get no pointer events, so find the link from the click's text position.</summary>
    private void MakeClickable(SelectableTextBlock block, List<(int Start, int End, LinkTarget Target)> links)
    {
        LinkTarget? TargetAt(PointerEventArgs e)
        {
            var hit = block.TextLayout.HitTestPoint(e.GetPosition(block));
            if (!hit.IsInside)
                return null;
            foreach (var (start, end, target) in links)
            {
                if (hit.TextPosition >= start && hit.TextPosition < end)
                    return target;
            }
            return null;
        }

        block.PointerMoved += (_, e) => block.Cursor = TargetAt(e) is null ? null : new Cursor(StandardCursorType.Hand);
        block.PointerReleased += (_, e) =>
        {
            // A drag that selected text is not a click.
            if (e.InitialPressMouseButton == MouseButton.Left && block.SelectionStart == block.SelectionEnd && TargetAt(e) is { } target)
                navigate(target);
        };
    }

    /// <summary>The plain text of inlines or a table cell, for heading slugs and column widths.</summary>
    private static string Plain(ContainerBlock block) =>
        string.Concat(block.OfType<ParagraphBlock>().Select(p => Plain(p.Inline)));

    private static string Plain(ContainerInline? container)
    {
        if (container is null)
            return "";
        var text = new System.Text.StringBuilder();
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: text.Append(literal.Content.ToString()); break;
                case CodeInline code: text.Append(code.Content); break;
                case AutolinkInline auto: text.Append(auto.Url); break;
                case LineBreakInline: text.Append(' '); break;
                case LinkInline { IsImage: true }: break;
                case ContainerInline nested: text.Append(Plain(nested)); break;
            }
        }
        return text.ToString();
    }
}
