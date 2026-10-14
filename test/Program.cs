// Headless check of store + sync (two peers in one process over real UDP/TCP) and the Markdown renderer.
// Run: dotnet run --project test
// Real JackTheClipper peers on the LAN will sync into these stores too, so checks only look at this test's own ids.
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using JackTheClipper;

var dir = Directory.CreateTempSubdirectory("jtc-").FullName;
var a = new Store(Path.Combine(dir, "a.json"));
var b = new Store(Path.Combine(dir, "b.json"));
using var sa = new Sync(a);
using var sb = new Sync(b);

a.Put(N("pub", pub: true));
a.Put(N("priv"));
await Until(() => Has(b, "pub"), "public note to reach peer");
Check(!Has(b, "priv"), "private note leaked");
Check(b.List().Single(n => n.Id == "pub").Images.Single() == "iVBORw0K", "image survives sync");

a.Put(N("pub"));                                   // un-publishing removes it from peers
await Until(() => !Has(b, "pub"), "unpublish to propagate");
a.Put(N("pub", pub: true));
await Until(() => Has(b, "pub"), "republish");
b.Remove("pub");                                   // delete on B propagates back to A
await Until(() => !Has(a, "pub"), "tombstone to reach origin");

b.Merge([N("old", updated: Store.Now() - 31 * Store.Day), N("future", updated: Store.Now() + 2 * Store.Day), new Note(), null,
    .. JsonSerializer.Deserialize<List<Note?>>("""[{"id":"x","updated":1,"title":null}]""", Store.Json)!]);
Check(!new[] { "old", "future", "", "x" }.Any(id => Has(b, id)), "bad remote notes rejected");

b.Put(N("keep", pin: true));
b.Put(N("drop"));
b.Sweep(Store.Now() + 31 * Store.Day);
Check(Has(b, "keep") && !Has(b, "drop"), "30-day expiry spares pinned");

b.Flush();
var reloaded = new Store(Path.Combine(dir, "b.json"));
Check(Has(reloaded, "keep") && !Has(reloaded, "drop"), "persistence");

Exception? mdFailure = null; // WPF documents need an STA thread
var sta = new Thread(() => { try { CheckMarkdown(); } catch (Exception e) { mdFailure = e; } });
sta.SetApartmentState(ApartmentState.STA);
sta.Start();
sta.Join();
if (mdFailure != null) throw mdFailure;
Console.WriteLine("ok");

static void CheckMarkdown()
{
    // Live styling: build paragraphs the way the editor does, style them, and look at the formatting.
    var lines = new[] { "# Title", "**bold** *it* `code` ~~gone~~ snake_case_name", "- [x] done", "> quote", "```", "x = **1**", "```", "after" };
    var doc = new FlowDocument();
    foreach (var l in lines) doc.Blocks.Add(new Paragraph(new Run(l)));
    Markdown.Style(doc);
    var ps = doc.Blocks.OfType<Paragraph>().ToArray();
    object Look(int line, string part, DependencyProperty dp)
    {
        var at = lines[line].IndexOf(part);
        return new TextRange(Markdown.At(ps[line], at), Markdown.At(ps[line], at + part.Length)).GetPropertyValue(dp);
    }
    bool Has(object decorations, TextDecorationLocation where) => decorations is TextDecorationCollection c && c.Any(d => d.Location == where);
    Check(ps[0].FontSize == 24 && ps[0].FontWeight == FontWeights.SemiBold, "heading");
    Check(Look(1, "bold", TextElement.FontWeightProperty).Equals(FontWeights.Bold), "bold");
    Check(Look(1, "it", TextElement.FontStyleProperty).Equals(FontStyles.Italic), "italic");
    Check(!Look(1, "case", TextElement.FontStyleProperty).Equals(FontStyles.Italic), "snake_case stays plain");
    Check(Look(1, "code", TextElement.FontFamilyProperty) is FontFamily f && f.Source.Contains("Mono"), "inline code");
    Check(Has(Look(1, "gone", Inline.TextDecorationsProperty), TextDecorationLocation.Strikethrough), "strikethrough");
    Check(Has(Look(2, "done", Inline.TextDecorationsProperty), TextDecorationLocation.Strikethrough), "done task is struck through");
    Check(ps[3].BorderThickness.Left == 3, "quote bar");
    Check(ps[5].FontFamily.Source.Contains("Mono") && !Look(5, "1", TextElement.FontWeightProperty).Equals(FontWeights.Bold), "code block is literal");
    Check(!ps[7].FontFamily.Source.Contains("Mono"), "code block ends at the closing fence");
    Check(new TextRange(ps[1].ContentStart, ps[1].ContentEnd).Text == lines[1], "styling never changes the text");

    // Restyling after an edit: the heading marker removed, so the line is no longer a heading.
    ((Run)ps[0].Inlines.FirstInline).Text = "Title";
    Markdown.Style(doc);
    Check(ps[0].FontSize != 24, "edited line restyles");

    // Character offsets map to positions and back, with an image counting as one character.
    var img = new Paragraph();
    img.Inlines.Add(new Run("ab"));
    img.Inlines.Add(new InlineUIContainer(new System.Windows.Shapes.Rectangle()));
    img.Inlines.Add(new Run("cd"));
    new FlowDocument(img);
    Check(Markdown.TextOf(img) == "ab" + Store.ImageMark + "cd", "image is one character");
    Check(Enumerable.Range(0, 6).All(k => Markdown.OffsetIn(img, Markdown.At(img, k)) == k), "offset round trip");

    Check(Markdown.LinkAt("see https://jtc.dev/a. now", 8)?.AbsoluteUri == "https://jtc.dev/a", "bare link");
    Check(Markdown.LinkAt("[site](https://example.com)", 2)?.Host == "example.com", "labelled link");
    Check(Markdown.LinkAt("[bad](javascript:alert(1))", 2) == null && Markdown.LinkAt("[f](file:///C:/x)", 1) == null, "unsafe links refused");
    Check(Markdown.Plain("# Head\n- **a** [l](https://x.y)\n> *q*") == "Head\n• a l\nq", "plain text for cards");
}

static Note N(string id, bool pub = false, bool pin = false, long updated = 0) =>
    new() { Id = id, Title = id, Text = "hi" + Store.ImageMark, Images = ["iVBORw0K"], Public = pub, Pinned = pin, Updated = updated };
static bool Has(Store s, string id) => s.List().Any(n => n.Id == id);
static void Check(bool ok, string what) { if (!ok) throw new Exception("FAILED: " + what); }
static async Task Until(Func<bool> cond, string what)
{
    for (var i = 0; i < 100; i++) { if (cond()) return; await Task.Delay(100); }
    throw new Exception("timed out waiting for " + what);
}
