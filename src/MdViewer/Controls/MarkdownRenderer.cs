using System.Text.RegularExpressions;
using MdViewer.Core.Markdown;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI.Text;

namespace MdViewer.Controls;

/// <summary>Builds native WinUI elements for the C++ core's document model. No browser or WebView is involved.</summary>
internal sealed partial class MarkdownRenderer(double zoom, string? baseDirectory, Action<string> linkInvoked)
{
    private static readonly FontFamily BodyFont = new("Segoe UI Variable Text, Segoe UI");
    private static readonly FontFamily DisplayFont = new("Segoe UI Variable Display, Segoe UI");
    private static readonly FontFamily MonoFont = new("Cascadia Mono, Cascadia Code, Consolas");
    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");
    private static readonly double[] HeadingSizes = [30, 24, 20, 17.5, 16, 15];
    private static readonly string[] Bullets = ["•", "◦", "▪"];

    [GeneratedRegex(@"^<br\s*/?>$", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreakTag();

    private double S(double size) => Math.Round(size * zoom, 1);
    private double BodySize => S(15);
    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>A top-level block with the vertical rhythm of the page.</summary>
    public FrameworkElement Build(MarkdownBlock block, bool first)
    {
        var element = Block(block, 0, quiet: false);
        if (element.Visibility == Visibility.Collapsed) return element;
        element.Margin = block.Kind == BlockKind.Heading
            ? new Thickness(0, first ? 0 : S(block.Level <= 2 ? 30 : 22), 0, S(10))
            : new Thickness(0, 0, 0, S(16));
        return element;
    }

    private FrameworkElement Block(MarkdownBlock block, int listDepth, bool quiet) => block.Kind switch
    {
        BlockKind.Paragraph => Paragraph(block, quiet),
        BlockKind.Heading => Heading(block),
        BlockKind.Code => CodeBlock(block.Literal, block.Language.StartsWith("{=", StringComparison.Ordinal) ? "raw " + block.Language.Trim('{', '}', '=') : block.Language, quiet: false),
        BlockKind.FrontMatter => CodeBlock(block.Literal, "front matter", quiet: true),
        BlockKind.Html when block.IsHtmlComment => new Border { Visibility = Visibility.Collapsed },
        BlockKind.Html => CodeBlock(block.Literal, "html", quiet: true),
        BlockKind.Quote => new Border
        {
            BorderBrush = Brush("QuoteBarBrush"),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding = new Thickness(S(18), S(2), 0, S(2)),
            Child = Stack(block.Blocks, listDepth, quiet: true, S(12))
        },
        BlockKind.BulletList or BlockKind.OrderedList => List(block, listDepth, quiet),
        BlockKind.ThematicBreak => new Border { Height = 1, Background = Brush("LineBrush"), Margin = new Thickness(0, S(10), 0, S(10)) },
        BlockKind.Table => Table(block, quiet),
        _ => Stack(block.Blocks, listDepth, quiet, S(12))
    };

    private StackPanel Stack(IEnumerable<MarkdownBlock> blocks, int listDepth, bool quiet, double spacing)
    {
        var panel = new StackPanel { Spacing = spacing };
        foreach (var child in blocks)
        {
            var element = Block(child, listDepth, quiet);
            if (element.Visibility == Visibility.Visible) panel.Children.Add(element);
        }
        return panel;
    }

    private FrameworkElement Paragraph(MarkdownBlock block, bool quiet)
    {
        // A paragraph holding only an image (the usual screenshot or figure) becomes a block-level
        // image, so it can use the column's full width.
        var visible = block.Inlines.Where(i => i.Kind != InlineKind.SoftBreak && !(i.Kind == InlineKind.Text && string.IsNullOrWhiteSpace(i.Text))).ToList();
        if (visible is [{ Kind: InlineKind.Image } image] && ImageElement(image, inline: false) is { } picture) return picture;
        return Text(block.Inlines, BodySize, 1.6, quiet ? Brush("MutedBrush") : Brush("TextBrush"));
    }

    private FrameworkElement Heading(MarkdownBlock block)
    {
        var level = Math.Clamp(block.Level, 1, 6);
        var text = Text(StripAttributeBlock(block.Inlines), S(HeadingSizes[level - 1]), 1.3, level == 6 ? Brush("MutedBrush") : Brush("TextBrush"), DisplayFont);
        text.FontWeight = level == 1 ? FontWeights.Bold : FontWeights.SemiBold;
        if (level > 2) return text;
        var panel = new StackPanel { Spacing = S(8) };
        panel.Children.Add(text);
        panel.Children.Add(new Border { Height = 1, Background = Brush("LineBrush") });
        return panel;
    }

    private FrameworkElement CodeBlock(string code, string label, bool quiet)
    {
        var panel = new StackPanel { Spacing = S(6) };
        if (label.Length > 0)
            panel.Children.Add(new TextBlock { Text = label.ToUpperInvariant(), FontSize = S(10.5), CharacterSpacing = 80, Foreground = Brush("QuietBrush") });
        panel.Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            Content = new TextBlock
            {
                Text = code.TrimEnd('\n', '\r'),
                FontFamily = MonoFont,
                FontSize = S(13.5),
                LineHeight = S(20),
                IsTextSelectionEnabled = true,
                Foreground = quiet ? Brush("MutedBrush") : Brush("CodeTextBrush"),
                Padding = new Thickness(0, 0, 0, S(4))
            }
        });
        return new Border
        {
            Background = Brush("CodeSurfaceBrush"),
            BorderBrush = Brush("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(S(16), S(12), S(16), S(10)),
            Child = panel
        };
    }

    private StackPanel List(MarkdownBlock list, int depth, bool quiet)
    {
        var panel = new StackPanel { Spacing = list.IsTight ? S(4) : S(10) };
        var number = list.Start;
        foreach (var item in list.Blocks)
        {
            var marker = new TextBlock
            {
                FontSize = BodySize,
                LineHeight = BodySize * 1.6,
                MinWidth = S(20),
                TextAlignment = TextAlignment.Right,
                Foreground = Brush("MutedBrush"),
                Text = list.Kind == BlockKind.OrderedList ? $"{number++}." : Bullets[depth % Bullets.Length]
            };
            if (item.Task != TaskState.None)
            {
                marker.Text = item.Task == TaskState.Done ? "☑" : "☐";
                marker.FontFamily = SymbolFont;
                marker.Foreground = item.Task == TaskState.Done ? Brush("AccentBrush") : Brush("MutedBrush");
            }
            var content = Stack(item.Blocks, depth + 1, quiet, S(8));
            var row = new Grid { ColumnSpacing = S(10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.Children.Add(marker);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            panel.Children.Add(row);
        }
        return panel;
    }

    private ScrollViewer Table(MarkdownBlock table, bool quiet)
    {
        var rows = table.Blocks.SelectMany(section => section.Blocks.Select(row => (Row: row, Header: section.Kind == BlockKind.TableHead))).ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Row.Blocks.Count);
        var grid = new Grid();
        for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < rows[r].Row.Blocks.Count; c++)
            {
                var cell = rows[r].Row.Blocks[c];
                var text = Text(cell.Inlines, S(14), 1.5, quiet ? Brush("MutedBrush") : Brush("TextBrush"));
                text.MaxWidth = S(520);
                text.TextAlignment = cell.Alignment switch { CellAlignment.Center => TextAlignment.Center, CellAlignment.Right => TextAlignment.Right, _ => TextAlignment.Left };
                if (rows[r].Header) text.FontWeight = FontWeights.SemiBold;
                var border = new Border
                {
                    BorderBrush = Brush("LineBrush"),
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Background = rows[r].Header ? Brush("SurfaceBrush") : r % 2 == 0 ? Brush("SidebarBrush") : null,
                    Padding = new Thickness(S(12), S(7), S(12), S(7)),
                    Child = text
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                grid.Children.Add(border);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = grid
        };
    }

    private RichTextBlock Text(IEnumerable<MarkdownInline> inlines, double size, double lineSpacing, Brush foreground, FontFamily? font = null)
    {
        var paragraph = new Paragraph();
        AddInlines(paragraph.Inlines, inlines, size, inLink: false);
        var text = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontFamily = font ?? BodyFont,
            FontSize = size,
            LineHeight = Math.Round(size * lineSpacing),
            Foreground = foreground,
            SelectionHighlightColor = (SolidColorBrush)Brush("QuoteBarBrush")
        };
        text.Blocks.Add(paragraph);
        return text;
    }

    private void AddInlines(InlineCollection target, IEnumerable<MarkdownInline> inlines, double size, bool inLink)
    {
        foreach (var inline in inlines)
        {
            switch (inline.Kind)
            {
                case InlineKind.Text: target.Add(new Run { Text = inline.Text }); break;
                case InlineKind.SoftBreak: target.Add(new Run { Text = " " }); break;
                case InlineKind.LineBreak: target.Add(new LineBreak()); break;
                case InlineKind.Emphasis: target.Add(Wrap(new Italic(), inline, size, inLink)); break;
                case InlineKind.Strong: target.Add(Wrap(new Bold(), inline, size, inLink)); break;
                case InlineKind.Underline: target.Add(Wrap(new Underline(), inline, size, inLink)); break;
                case InlineKind.Strikethrough: target.Add(Wrap(new Span { TextDecorations = TextDecorations.Strikethrough }, inline, size, inLink)); break;
                case InlineKind.Code:
                    target.Add(new Run { Text = inline.Text, FontFamily = MonoFont, FontSize = Math.Round(size * 0.9, 1), Foreground = Brush("CodeTextBrush") });
                    break;
                case InlineKind.Html:
                    if (LineBreakTag().IsMatch(inline.Text.Trim())) target.Add(new LineBreak());
                    break;
                case InlineKind.Image:
                    if (!inLink && ImageElement(inline, inline: true) is { } image) target.Add(new InlineUIContainer { Child = image });
                    else target.Add(new Run { Text = AltText(inline), Foreground = Brush("MutedBrush") });
                    break;
                case InlineKind.Link when inLink:
                    AddInlines(target, inline.Children, size, inLink);
                    break;
                case InlineKind.Link:
                {
                    var link = new Hyperlink { Foreground = Brush("LinkBrush"), UnderlineStyle = UnderlineStyle.Single };
                    AddInlines(link.Inlines, inline.Children, size, inLink: true);
                    if (link.Inlines.Count == 0) link.Inlines.Add(new Run { Text = inline.Url });
                    var url = inline.Url;
                    link.Click += (_, _) => linkInvoked(url);
                    ToolTipService.SetToolTip(link, string.IsNullOrEmpty(inline.Title) ? url : $"{inline.Title}\n{url}");
                    target.Add(link);
                    break;
                }
            }
        }
    }

    private Span Wrap(Span span, MarkdownInline inline, double size, bool inLink)
    {
        AddInlines(span.Inlines, inline.Children, size, inLink);
        return span;
    }

    private static string AltText(MarkdownInline image) => image.PlainText() is { Length: > 0 } alt ? $"[{alt}]" : "[image]";

    private FrameworkElement? ImageElement(MarkdownInline image, bool inline)
    {
        if (ResolveImage(image.Url) is not { } uri) return null;
        var element = new Image { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, MaxHeight = S(1400) };
        if (uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            // SVG has no pixel size to honor: inline ones are usually badges, standalone ones figures.
            var svg = new SvgImageSource(uri);
            if (inline) element.Height = S(20); else element.MaxWidth = S(720);
            svg.OpenFailed += (_, _) => element.Visibility = Visibility.Collapsed;
            element.Source = svg;
        }
        else
        {
            var bitmap = new BitmapImage(uri);
            // Show images at their natural size (scaled with zoom), shrinking only to fit the column.
            bitmap.ImageOpened += (_, _) => element.MaxWidth = bitmap.PixelWidth * zoom;
            bitmap.ImageFailed += (_, _) => element.Visibility = Visibility.Collapsed;
            element.Source = bitmap;
        }
        var alt = image.PlainText();
        if (alt.Length > 0) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, alt);
        if ((image.Title.Length > 0 ? image.Title : alt) is { Length: > 0 } tip) ToolTipService.SetToolTip(element, tip);
        return element;
    }

    private Uri? ResolveImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme is "http" or "https") return absolute;
            return absolute.IsFile && File.Exists(absolute.LocalPath) ? absolute : null;
        }
        if (baseDirectory is null) return null;
        var relative = url.Split('#', '?')[0];
        try
        {
            var path = Path.GetFullPath(Path.Combine(baseDirectory, Uri.UnescapeDataString(relative).Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(path) ? new Uri(path) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException) { return null; }
    }

    // Pandoc writes "Title {#id .class}"; the outline already strips it, so the page does too.
    private static IEnumerable<MarkdownInline> StripAttributeBlock(List<MarkdownInline> inlines)
    {
        if (inlines is not [.., { Kind: InlineKind.Text } last]) return inlines;
        var text = last.Text.TrimEnd();
        var open = text.LastIndexOf('{');
        if (!text.EndsWith('}') || open < 0 || open + 1 >= text.Length || !(text[open + 1] is '#' or '.' or '-' || text.IndexOf('=', open) > 0)) return inlines;
        return [.. inlines.Take(inlines.Count - 1), new MarkdownInline(InlineKind.Text) { Text = text[..open].TrimEnd() }];
    }
}
