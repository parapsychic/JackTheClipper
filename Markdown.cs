// Live Markdown for the editor: paragraphs are styled in place while you type, and the text (syntax included) stays
// exactly as typed, so notes remain plain Markdown. Plain() gives the syntax-free text for the notes list.
// ponytail: headings, lists (nested, numbered, tasks), quotes, code, rules, emphasis, links; no tables/HTML/footnotes.
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace JackTheClipper;

public static class Markdown
{
    static readonly Regex Heading = new(@"^(#{1,6})\s+(.*?)(\s+#+)?\s*$");
    static readonly Regex ListLine = new(@"^(\s*)(?:([-*+])|(\d{1,9})[.)])\s+(?:\[([ xX])\]\s+)?(.*)$");
    static readonly Regex Rule = new(@"^ {0,3}([-*_])( *\1){2,} *$");
    static readonly Regex Quote = new(@"^\s*>\s?(.*)$");
    static readonly Regex Fence = new(@"^\s*(```|~~~)");
    static readonly Regex InlineSyntax = new(
        @"(?<tick>`+)(?<code>.+?)\k<tick>" +
        @"|\*\*\*(?<bi>.+?)\*\*\*" +
        @"|\*\*(?<b>.+?)\*\*|(?<!\w)__(?<b>.+?)__(?!\w)" +
        @"|~~(?<s>.+?)~~" +
        @"|\*(?<i>[^*\s](?:.*?[^*\s])?)\*|(?<!\w)_(?<i>[^_\s](?:.*?[^_\s])?)_(?!\w)" +
        @"|\[(?<label>[^\]]+)\]\((?<url>[^)\s]+)\)" +
        @"|(?<auto>https?://[^\s<>()\[\]]*[^\s<>()\[\].,;:!?'""])");
    static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Courier New");
    static readonly double[] HeadingSize = [24, 20, 18, 16, 15, 14];
    static readonly DependencyProperty[] ParagraphLook =
    [
        TextElement.FontSizeProperty, TextElement.FontWeightProperty, TextElement.FontFamilyProperty, TextElement.ForegroundProperty,
        TextElement.BackgroundProperty, Block.BorderBrushProperty, Block.BorderThicknessProperty, Block.PaddingProperty, Block.LineHeightProperty,
    ];
    // What each paragraph was last styled for, so a keystroke only restyles the paragraphs it changed.
    static readonly ConditionalWeakTable<Paragraph, string> Styled = new();

    public static void Style(FlowDocument doc)
    {
        var inCode = false;
        foreach (var p in doc.Blocks.OfType<Paragraph>().ToList()) // styling edits the document, which ends any live enumeration
        {
            var text = TextOf(p);
            var fence = Fence.IsMatch(text);
            var kind = fence ? 'f' : inCode ? 'c' : 'm'; // fence line, inside a code block, Markdown
            if (fence) inCode = !inCode;
            var key = kind + text;
            if (Styled.TryGetValue(p, out var was) && was == key) continue;
            Styled.AddOrUpdate(p, key);
            StyleParagraph(p, text, kind);
        }
    }

    static void StyleParagraph(Paragraph p, string text, char kind)
    {
        new TextRange(p.ContentStart, p.ContentEnd).ClearAllProperties();
        foreach (var dp in ParagraphLook) p.ClearValue(dp);

        if (kind != 'm')
        {
            p.FontFamily = Mono;
            p.FontSize = 14;
            p.Padding = new Thickness(12, 0, 12, 0);
            p.Background = Theme("SubtleFillColorSecondaryBrush");
            if (kind == 'f') Set(p, 0, text.Length, TextElement.ForegroundProperty, Theme("TextFillColorTertiaryBrush"));
            return;
        }
        if (Rule.IsMatch(text))
        {
            Dim(p, 0, text.Length);
            p.BorderThickness = new Thickness(0, 0, 0, 1);
            p.BorderBrush = Theme("DividerStrokeColorDefaultBrush");
            return;
        }

        var body = 0; // where the line's content starts, after its block marker
        if (Heading.Match(text) is { Success: true } h)
        {
            p.FontSize = HeadingSize[h.Groups[1].Length - 1];
            p.FontWeight = FontWeights.SemiBold;
            p.LineHeight = double.NaN;
            body = h.Groups[2].Index;
            Dim(p, 0, body);
            if (h.Groups[3].Success) Dim(p, h.Groups[3].Index, h.Groups[3].Index + h.Groups[3].Length);
        }
        else if (ListLine.Match(text) is { Success: true } li)
        {
            body = li.Groups[5].Index;
            Set(p, li.Groups[1].Length, body, TextElement.ForegroundProperty, Theme("AccentTextFillColorPrimaryBrush"));
            if (li.Groups[4].Value is "x" or "X") // done task
            {
                Set(p, body, text.Length, Inline.TextDecorationsProperty, TextDecorations.Strikethrough);
                Set(p, body, text.Length, TextElement.ForegroundProperty, Theme("TextFillColorSecondaryBrush"));
            }
        }
        else if (Quote.Match(text) is { Success: true } q)
        {
            body = q.Groups[1].Index;
            p.BorderThickness = new Thickness(3, 0, 0, 0);
            p.BorderBrush = Theme("AccentFillColorDefaultBrush");
            p.Padding = new Thickness(12, 0, 0, 0);
            p.Foreground = Theme("TextFillColorSecondaryBrush");
            Dim(p, 0, body);
        }
        StyleInline(p, text, body, text.Length);
    }

    static void StyleInline(Paragraph p, string text, int from, int to)
    {
        for (var m = InlineSyntax.Match(text, from, to - from); m.Success; m = m.NextMatch())
        {
            int s = m.Index, e = m.Index + m.Length;
            if (m.Groups["code"].Success)
            {
                Set(p, s, e, TextElement.FontFamilyProperty, Mono);
                Set(p, s, e, TextElement.BackgroundProperty, Theme("SubtleFillColorSecondaryBrush"));
                var tick = m.Groups["tick"].Length;
                Dim(p, s, s + tick);
                Dim(p, e - tick, e);
                continue;
            }
            if (m.Groups["auto"].Success) { LinkLook(p, s, e); continue; }
            if (m.Groups["label"].Success)
            {
                var label = m.Groups["label"];
                LinkLook(p, label.Index, label.Index + label.Length);
                Dim(p, s, label.Index);
                Dim(p, label.Index + label.Length, e);
                StyleInline(p, text, label.Index, label.Index + label.Length);
                continue;
            }
            var g = new[] { "bi", "b", "i", "s" }.Select(n => m.Groups[n]).First(x => x.Success);
            if (g.Name is "bi" or "b") Set(p, s, e, TextElement.FontWeightProperty, FontWeights.Bold);
            if (g.Name is "bi" or "i") Set(p, s, e, TextElement.FontStyleProperty, FontStyles.Italic);
            if (g.Name is "s") Set(p, s, e, Inline.TextDecorationsProperty, TextDecorations.Strikethrough);
            Dim(p, s, g.Index);
            Dim(p, g.Index + g.Length, e);
            StyleInline(p, text, g.Index, g.Index + g.Length); // emphasis inside emphasis
        }
    }

    static void LinkLook(Paragraph p, int s, int e)
    {
        Set(p, s, e, TextElement.ForegroundProperty, Theme("AccentTextFillColorPrimaryBrush"));
        Set(p, s, e, Inline.TextDecorationsProperty, TextDecorations.Underline);
    }

    static void Dim(Paragraph p, int s, int e) => Set(p, s, e, TextElement.ForegroundProperty, Theme("TextFillColorTertiaryBrush"));

    static void Set(Paragraph p, int s, int e, DependencyProperty dp, object value)
    {
        if (e > s) new TextRange(At(p, s), At(p, e)).ApplyPropertyValue(dp, value);
    }

    static Brush Theme(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    // A clickable link (http, https or mailto only: notes come from other people) at a character offset, if any.
    public static Uri? LinkAt(string text, int offset)
    {
        foreach (Match m in InlineSyntax.Matches(text))
            if (offset >= m.Index && offset < m.Index + m.Length && (m.Groups["url"].Success || m.Groups["auto"].Success))
                return Uri.TryCreate(m.Groups["url"].Success ? m.Groups["url"].Value : m.Value, UriKind.Absolute, out var uri)
                    && uri.Scheme is "http" or "https" or "mailto" ? uri : null;
        return null;
    }

    // A paragraph's text as notes store it: each image is one Store.ImageMark.
    public static string TextOf(Paragraph p)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(InlineCollection xs)
        {
            foreach (var x in xs)
                switch (x)
                {
                    case Run r: sb.Append(r.Text); break;
                    case Span s: Walk(s.Inlines); break;
                    case InlineUIContainer: sb.Append(Store.ImageMark); break;
                    case LineBreak: sb.Append('\n'); break;
                }
        }
        Walk(p.Inlines);
        return sb.ToString();
    }

    // Character offset (as in TextOf) → position in the paragraph, and back.
    public static TextPointer At(Paragraph p, int offset)
    {
        var nav = p.ContentStart;
        while (nav.CompareTo(p.ContentEnd) < 0)
        {
            var ctx = nav.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                var run = nav.GetTextRunLength(LogicalDirection.Forward);
                if (offset <= run) return nav.GetPositionAtOffset(offset)!;
                offset -= run;
                nav = nav.GetPositionAtOffset(run)!;
                continue;
            }
            if (OneChar(nav, ctx))
            {
                if (offset == 0) return nav;
                offset--;
            }
            nav = nav.GetNextContextPosition(LogicalDirection.Forward)!;
        }
        return p.ContentEnd;
    }

    public static int OffsetIn(Paragraph p, TextPointer pos)
    {
        var n = 0;
        for (var nav = p.ContentStart; nav.CompareTo(pos) < 0;)
        {
            var ctx = nav.GetPointerContext(LogicalDirection.Forward);
            if (ctx == TextPointerContext.Text)
            {
                var run = Math.Min(nav.GetTextRunLength(LogicalDirection.Forward), nav.GetOffsetToPosition(pos));
                n += run;
                nav = nav.GetPositionAtOffset(run)!;
                continue;
            }
            if (OneChar(nav, ctx)) n++;
            nav = nav.GetNextContextPosition(LogicalDirection.Forward)!;
        }
        return n;
    }

    // An image counts as one character, and so does a line break.
    static bool OneChar(TextPointer nav, TextPointerContext ctx) =>
        ctx == TextPointerContext.EmbeddedElement || (ctx == TextPointerContext.ElementStart && nav.GetAdjacentElement(LogicalDirection.Forward) is LineBreak);

    // The notes list shows text without the syntax: "# Groceries" reads "Groceries", "**milk**" reads "milk".
    public static string Plain(string md)
    {
        var lines = new List<string>();
        foreach (var line in md.Split('\n'))
        {
            if (Fence.IsMatch(line) || Rule.IsMatch(line)) continue;
            var s = Heading.Match(line) is { Success: true } h ? h.Groups[2].Value
                : ListLine.Match(line) is { Success: true } li ? li.Groups[1].Value
                    + (li.Groups[4].Success ? (li.Groups[4].Value == " " ? "☐ " : "☑ ") : li.Groups[3].Success ? li.Groups[3].Value + ". " : "• ")
                    + li.Groups[5].Value
                : Quote.Match(line) is { Success: true } q ? q.Groups[1].Value
                : line;
            for (var pass = 0; pass < 3; pass++) // nested emphasis
                s = InlineSyntax.Replace(s, m => m.Groups["auto"].Success ? m.Value
                    : new[] { "code", "bi", "b", "i", "s", "label" }.Select(g => m.Groups[g]).First(g => g.Success).Value);
            lines.Add(s);
        }
        return string.Join('\n', lines);
    }
}
