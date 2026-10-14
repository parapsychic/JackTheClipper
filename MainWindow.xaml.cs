using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace JackTheClipper;

public partial class MainWindow : Window
{
    const string Mark = Store.ImageMark;
    // Segoe Fluent Icons code points (Segoe MDL2 Assets on Windows 10 has the same ones)
    const string IconPin = "\uE718", IconUnpin = "\uE77A", IconCloud = "\uE753", IconLock = "\uE72E", IconCopy = "\uE8C8",
        IconDelete = "\uE74D", IconQuit = "\uE7E8", IconCut = "\uE8C6", IconPaste = "\uE77F", IconAdd = "\uE710";
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    static readonly byte[] PngMagic = [137, 80, 78, 71, 13, 10, 26, 10];

    readonly Store store;
    readonly Sync sync;
    readonly System.Windows.Forms.NotifyIcon tray;
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    readonly Dictionary<string, (long Updated, BitmapSource? Img)> thumbs = [];
    readonly ConditionalWeakTable<string, BitmapSource> decoded = new(); // by image string reference, so undo doesn't re-decode
    readonly Stack<Snap> undo = new(), redo = new();
    Snap last = new("", [], 0);
    long lastEdit;
    int lastLines;
    Note? cur;
    bool quitting, loading, styling, restylePending;

    record Snap(string Text, List<string> Images, int Caret);

    [STAThread]
    static void Main()
    {
        // One instance per session: launching it again just pops the running window.
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, "JackTheClipper.Show", out var first);
        if (!first) { signal.Set(); return; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
#pragma warning disable WPF0001 // the built-in Windows 11 (Fluent) theme is still flagged experimental
        app.ThemeMode = ThemeMode.System; // follows Windows light/dark mode
#pragma warning restore WPF0001
        var win = new MainWindow();
        ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => win.Dispatcher.InvokeAsync(win.Reveal), null, -1, false);
        app.SessionEnding += (_, _) => win.SaveAll();
        win.Reveal();
        app.Run();
    }

    // Stays resident (tray + hotkey) so opening is just showing a loaded window, and peers can sync anytime.
    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 48); // 800x800, unless the screen is smaller
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 48);
        store = new Store(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "JackTheClipper", "notes.json"));
        store.Sweep();
        store.Changed += () => Dispatcher.InvokeAsync(() => { if (cur == null) RenderCards(); });
        sync = new Sync(store);
        new DispatcherTimer(TimeSpan.FromHours(1), DispatcherPriority.Background, (_, _) => store.Sweep(), Dispatcher);
        new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            var n = sync.Peers;
            PeersText.Text = n switch { 0 => "No peers on this network", 1 => "1 peer online", _ => $"{n} peers online" };
            PeersDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, n > 0 ? "AccentFillColorDefaultBrush" : "TextFillColorTertiaryBrush");
        }, Dispatcher);

        saveTimer.Tick += (_, _) => Save();
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); Toast.Visibility = Visibility.Collapsed; };
        TitleBox.TextChanged += (_, _) => { TitleHint.Visibility = Vis(TitleBox.Text.Length == 0); Dirty(); };
        TitleBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Editor.Focus(); };
        Editor.TextChanged += (_, _) =>
        {
            if (styling) return; // our own formatting
            BodyHint.Visibility = Vis(ReadEditor().Text.Length == 0);
            // Live Markdown: only the look changes, the text stays as typed. Styled just after this event, not inside it:
            // editing the document mid-TextChanged fights Windows' text input service and can drop what was typed.
            if (!restylePending)
            {
                restylePending = true;
                Dispatcher.InvokeAsync(() =>
                {
                    restylePending = false;
                    styling = true;
                    Markdown.Style(Editor.Document);
                    styling = false;
                });
            }
            if (loading) return;
            Record();
            Dirty();
        };
        Editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (_, _) => Step(undo, redo), (_, e) => { e.CanExecute = undo.Count > 0; e.Handled = true; }));
        Editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (_, _) => Step(redo, undo), (_, e) => { e.CanExecute = redo.Count > 0; e.Handled = true; }));
        // With the built-in undo off, the editor no longer maps these keys itself.
        Editor.InputBindings.Add(new KeyBinding(ApplicationCommands.Undo, Key.Z, ModifierKeys.Control));
        Editor.InputBindings.Add(new KeyBinding(ApplicationCommands.Redo, Key.Y, ModifierKeys.Control));
        Editor.InputBindings.Add(new KeyBinding(ApplicationCommands.Redo, Key.Z, ModifierKeys.Control | ModifierKeys.Shift));
        // Shift+Enter would make a line break inside a paragraph; notes are line-based, so make it a normal new line.
        Editor.CommandBindings.Add(new CommandBinding(EditingCommands.EnterLineBreak, (_, _) => EditingCommands.EnterParagraphBreak.Execute(null, Editor)));
        // Ctrl+click opens a link.
        Editor.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control || Editor.GetPositionFromPoint(e.GetPosition(Editor), true) is not { Paragraph: { } p } at) return;
            if (Markdown.LinkAt(Markdown.TextOf(p), Markdown.OffsetIn(p, at)) is not { } uri) return;
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        };
        // Every paste route (Ctrl+V, Shift+Insert, the context menu) ends up in Paste(); the command binding also
        // keeps Paste enabled when the clipboard holds only an image, which WPF alone would grey out.
        Editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Paste, (_, _) => Paste(), (_, e) => { e.CanExecute = true; e.Handled = true; }));
        DataObject.AddPastingHandler(Editor, (_, e) => { e.CancelCommand(); Paste(e.DataObject); });
        // Notes are plain text + images, so don't offer formatting that wouldn't survive a save.
        foreach (var c in new[] { EditingCommands.ToggleBold, EditingCommands.ToggleItalic, EditingCommands.ToggleUnderline,
                     EditingCommands.ToggleBullets, EditingCommands.ToggleNumbering, EditingCommands.AlignCenter, EditingCommands.AlignRight,
                     EditingCommands.AlignJustify, EditingCommands.IncreaseFontSize, EditingCommands.DecreaseFontSize,
                     EditingCommands.ToggleSubscript, EditingCommands.ToggleSuperscript })
            Editor.CommandBindings.Add(new CommandBinding(c, (_, _) => { }, (_, e) => { e.CanExecute = false; e.Handled = true; }));

        Deactivated += (_, _) => Flush();
        Closing += (_, e) => { if (!quitting) { e.Cancel = true; HideWindow(); } };
        SizeChanged += (_, _) => Cards.Columns = Math.Max(1, (int)((ActualWidth - 36) / 240));
        PreviewKeyDown += (_, e) =>
        {
            var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
            if (e.Key == Key.Escape) HideWindow();
            else if (ctrl && e.Key == Key.N) NewNote();
            else if (ctrl && e.Key is Key.W or Key.Q) Quit();
            else return;
            e.Handled = true;
        };

        // Right-click anywhere: text fields keep Cut/Copy/Paste, cards and ⋮ get note actions, the rest gets this.
        ContextMenu = Menu(Item(IconAdd, "New note", NewNote, "Ctrl+N"));
        Editor.ContextMenu = TextMenu(Editor);
        TitleBox.ContextMenu = TextMenu(TitleBox);

        tray = new() { Icon = TrayIcon(), Text = "Jack the Clipper (Ctrl+Shift+Space)", Visible = true, ContextMenuStrip = new() };
        tray.ContextMenuStrip.Items.Add("New note", null, (_, _) => { Reveal(); NewNote(); });
        tray.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Quit());
        tray.MouseClick += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) Reveal(); };
        NewNote(); // what the very first show opens on
    }

    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var round = 2; // DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND (Windows 11; ignored elsewhere)
        DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
        // Ctrl+Shift+Space from anywhere: MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, VK_SPACE
        if (!RegisterHotKey(hwnd, 1, 0x2 | 0x4 | 0x4000, 0x20)) Notify("Ctrl+Shift+Space is taken by another app");
        HwndSource.FromHwnd(hwnd).AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312) { if (IsActive) HideWindow(); else Reveal(); handled = true; } // WM_HOTKEY
            return IntPtr.Zero;
        });
    }

    // Hiding keeps everything, so this brings the window back exactly as it was: same view, note, caret and scroll.
    public void Reveal()
    {
        Show();
        Activate();
        if (FocusManager.GetFocusedElement(this) == null) Editor.Focus(); // only the very first show has nothing focused yet
    }

    void HideWindow() { Flush(); Hide(); }
    public void SaveAll() { Flush(); store.Flush(); }
    void Quit() { quitting = true; SaveAll(); sync.Dispose(); tray.Dispose(); Application.Current.Shutdown(); }

    // --- editing -------------------------------------------------------------------------------------------

    void Dirty() { if (cur == null) return; saveTimer.Stop(); saveTimer.Start(); }
    void Save() { saveTimer.Stop(); if (cur != null && !Empty()) store.Put(Snapshot()); }
    void Flush() { if (saveTimer.IsEnabled) Save(); }
    // Blank notes are never stored; a note emptied out is deleted when you leave it.
    void Leave() { if (cur == null) return; Flush(); if (Empty()) store.Remove(cur.Id); cur = null; }
    bool Empty() => TitleBox.Text.Trim().Length == 0 && ReadEditor().Text.Trim().Length == 0;
    Note Snapshot() { var (text, images) = ReadEditor(); return cur! with { Title = TitleBox.Text, Text = text, Images = images }; }

    void NewNote() => Open(new Note { Id = Guid.NewGuid().ToString() });

    // ponytail: an open note doesn't live-update from peers; last writer wins. The notes list does refresh live.
    void Open(Note n)
    {
        Leave();
        cur = n with { };
        TitleBox.Text = n.Title;
        LoadText(n.Text, n.Images);
        saveTimer.Stop(); // loading isn't an edit
        HistoryView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        Paint();
        Editor.Focus();
        Editor.CaretPosition = Editor.Document.ContentEnd;
        undo.Clear();
        redo.Clear();
        last = Capture();
        lastEdit = 0;
        lastLines = Editor.Document.Blocks.Count;
    }

    // --- undo ------------------------------------------------------------------------------------------------
    // Snapshots of the note's text: a new undo step starts with each new or removed line, or after a pause of over a second.
    // ponytail: unbounded per open note and cleared on switching notes; cap it if someone edits one note for days.

    Snap Capture()
    {
        var (text, images) = ReadEditor();
        var caret = Editor.CaretPosition;
        var offset = 0;
        foreach (var p in Editor.Document.Blocks.OfType<Paragraph>())
        {
            if (caret.Paragraph == p) { offset += Markdown.OffsetIn(p, caret); break; }
            offset += Markdown.TextOf(p).Length + 1;
        }
        return new Snap(text, images, offset);
    }

    void Record()
    {
        var now = Environment.TickCount64;
        var lines = Editor.Document.Blocks.Count;
        if (now - lastEdit > 1000 || lines != lastLines) undo.Push(last);
        redo.Clear();
        lastEdit = now;
        lastLines = lines;
        last = Capture();
    }

    void Step(Stack<Snap> from, Stack<Snap> to)
    {
        if (from.Count == 0) return;
        to.Push(Capture());
        var s = from.Pop();
        LoadText(s.Text, s.Images);
        var offset = s.Caret;
        foreach (var p in Editor.Document.Blocks.OfType<Paragraph>())
        {
            var len = Markdown.TextOf(p).Length;
            if (offset <= len) { Editor.CaretPosition = Markdown.At(p, offset).GetInsertionPosition(LogicalDirection.Forward); p.BringIntoView(); break; }
            offset -= len + 1;
        }
        last = s;
        lastEdit = 0;
        lastLines = Editor.Document.Blocks.Count;
        Dirty();
    }

    void Paint()
    {
        PubBtn.SetResourceReference(ForegroundProperty, cur!.Public ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
        PubBtn.ToolTip = cur.Public ? "Public: shared with peers (click to make private)" : "Private (click to share with peers)";
        PinMark.Visibility = Vis(cur.Pinned);
    }

    void LoadText(string text, List<string> images)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        var i = 0;
        foreach (var line in text.Split('\n'))
        {
            var p = new Paragraph();
            var parts = line.Split(Mark);
            for (var k = 0; k < parts.Length; k++)
            {
                if (k > 0 && i < images.Count && Decode(images[i++]) is { } b) p.Inlines.Add(new InlineUIContainer(Picture(b, images[i - 1])));
                if (parts[k].Length > 0) p.Inlines.Add(new Run(parts[k]));
            }
            doc.Blocks.Add(p);
        }
        loading = styling = true;
        Markdown.Style(doc);
        styling = false;
        Editor.Document = doc;
        loading = false;
    }

    BitmapSource? Decode(string png)
    {
        if (!decoded.TryGetValue(png, out var b) && Png(png) is { } fresh) decoded.AddOrUpdate(png, b = fresh);
        return b;
    }

    (string Text, List<string> Images) ReadEditor()
    {
        var text = new StringBuilder();
        var images = new List<string>();
        void Walk(InlineCollection xs)
        {
            foreach (var x in xs)
                switch (x)
                {
                    case Run r: text.Append(r.Text); break;
                    case LineBreak: text.Append('\n'); break;
                    case Span s: Walk(s.Inlines); break;
                    case InlineUIContainer { Child: Image { Tag: string png } }: text.Append(Mark); images.Add(png); break;
                }
        }
        var first = true;
        foreach (var p in Editor.Document.Blocks.OfType<Paragraph>())
        {
            if (!first) text.Append('\n');
            first = false;
            Walk(p.Inlines);
        }
        return (text.ToString(), images);
    }

    // Text pastes as plain text (no foreign formatting); otherwise any image on the clipboard goes in as an image.
    void Paste(IDataObject? d = null)
    {
        try
        {
            d ??= Clipboard.GetDataObject();
            if (d is null) return;
            if (d.GetData(DataFormats.UnicodeText) is string t && t.Trim().Length > 0)
            {
                Editor.Selection.Text = t;
                Editor.CaretPosition = Editor.Selection.End;
            }
            else foreach (var b in ClipboardImages(d)) InsertImage(b);
        }
        catch (ExternalException) { Notify("Couldn't read the clipboard"); }
    }

    static IEnumerable<BitmapSource> ClipboardImages(IDataObject d)
    {
        // PNG keeps transparency (browsers, Snipping Tool); copied image files arrive as a file list; DIB is the fallback.
        if (d.GetData("PNG") is MemoryStream png && Png(png.ToArray()) is { } p) return [p];
        if (d.GetData(DataFormats.FileDrop) is string[] files) return files.Select(LoadFile).OfType<BitmapSource>();
        return d.GetData(DataFormats.Bitmap) is BitmapSource b ? [b] : [];
    }

    static BitmapSource? LoadFile(string path)
    {
        try { return BitmapFrame.Create(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad); }
        catch (Exception) { return null; } // not an image
    }

    void InsertImage(BitmapSource b)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(b));
        using var ms = new MemoryStream();
        enc.Save(ms);
        Editor.Selection.Text = "";
        var c = new InlineUIContainer(Picture(b, Convert.ToBase64String(ms.ToArray())), Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward));
        Editor.CaretPosition = c.ElementEnd;
    }

    // The PNG rides along in Tag so saving never re-encodes.
    static Image Picture(BitmapSource b, string png) =>
        new() { Source = b, Tag = png, Stretch = Stretch.Uniform, MaxWidth = b.PixelWidth, Margin = new Thickness(0, 4, 0, 4) };

    // Images can come from untrusted peers: only ever hand WIC a PNG, and refuse decompression bombs.
    static BitmapSource? Png(string b64, int width = 0)
    {
        try { return Png(Convert.FromBase64String(b64), width); }
        catch (FormatException) { return null; }
    }

    static BitmapSource? Png(byte[] bytes, int width = 0)
    {
        if (bytes.Length < 24 || !bytes.AsSpan(0, 8).SequenceEqual(PngMagic)) return null;
        long w = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16)), h = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20));
        if (w * h > 50_000_000) return null;
        try
        {
            var b = new BitmapImage();
            b.BeginInit();
            b.StreamSource = new MemoryStream(bytes);
            b.CacheOption = BitmapCacheOption.OnLoad;
            if (width > 0 && width < w) b.DecodePixelWidth = width;
            b.EndInit();
            b.Freeze();
            return b;
        }
        catch (Exception) { return null; } // corrupt image: leave it out
    }

    // --- notes list ----------------------------------------------------------------------------------------

    void ShowHistory()
    {
        Leave();
        EditorView.Visibility = Visibility.Collapsed;
        HistoryView.Visibility = Visibility.Visible;
        RenderCards();
    }

    void RenderCards()
    {
        Cards.Children.Clear();
        foreach (var n in store.List().OrderByDescending(n => n.Pinned).ThenByDescending(n => n.Updated)) Cards.Children.Add(Card(n));
        EmptyHint.Visibility = Vis(Cards.Children.Count == 0);
    }

    UIElement Card(Note n)
    {
        // Untitled notes use their first line as the title.
        var text = Markdown.Plain(n.Text.Replace(Mark, "")).Trim();
        var nl = text.IndexOf('\n');
        var (title, rest) = n.Title.Trim().Length > 0 ? (n.Title, text) : nl < 0 ? (text, "") : (text[..nl], text[(nl + 1)..]);
        var thumb = Thumb(n);

        var copy = new Button { Content = IconCopy, Width = 32, Height = 32, FontSize = 14, ToolTip = "Copy", Visibility = Visibility.Hidden, Margin = new Thickness(4, -4, 0, 0) };
        copy.Click += (_, _) => Copy(n);
        var head = new DockPanel();
        DockPanel.SetDock(copy, Dock.Right);
        head.Children.Add(copy);
        head.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });

        var badges = new StackPanel { Orientation = Orientation.Horizontal };
        if (n.Pinned) badges.Children.Add(Glyph(IconPin, "Pinned"));
        if (n.Public) badges.Children.Add(Glyph(IconCloud, "Public: shared with peers"));
        var foot = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(badges, Dock.Right);
        foot.Children.Add(badges);
        foot.Children.Add(Themed(new TextBlock { Text = Ago(n.Updated), FontSize = 12 }, "TextFillColorTertiaryBrush"));

        var content = new StackPanel();
        if (rest.Length > 0)
            content.Children.Add(Themed(new TextBlock
            {
                Text = rest, FontSize = 13, LineHeight = 19, Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = thumb == null ? 114 : 38,
            }, "TextFillColorSecondaryBrush"));
        if (thumb != null)
            content.Children.Add(new Border
            {
                Height = 84, Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(6),
                Background = new ImageBrush(thumb) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top },
            });

        var body = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(foot, Dock.Bottom);
        body.Children.Add(head);
        body.Children.Add(foot);
        body.Children.Add(new Border { Child = content, ClipToBounds = true });

        var card = new Border
        {
            Child = body, Height = 200, Margin = new Thickness(6), Padding = new Thickness(16, 12, 12, 12),
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), ContextMenu = MenuFor(n),
        };
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.MouseEnter += (_, _) => { card.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush"); copy.Visibility = Visibility.Visible; };
        card.MouseLeave += (_, _) => { card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush"); copy.Visibility = Visibility.Hidden; };
        card.MouseLeftButtonUp += (_, _) => Open(n);
        return card;
    }

    BitmapSource? Thumb(Note n)
    {
        if (n.Images.Count == 0) return null;
        if (!thumbs.TryGetValue(n.Id, out var t) || t.Updated != n.Updated) thumbs[n.Id] = t = (n.Updated, Png(n.Images[0], 480));
        return t.Img;
    }

    static string Ago(long ms)
    {
        var d = TimeSpan.FromMilliseconds(Math.Max(0, Store.Now() - ms));
        return d.TotalMinutes < 1 ? "Just now"
            : d.TotalHours < 1 ? $"{(int)d.TotalMinutes} min ago"
            : d.TotalDays < 1 ? $"{(int)d.TotalHours} h ago"
            : DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime.ToString("d MMM");
    }

    // --- actions -------------------------------------------------------------------------------------------

    // Every right-click menu ends with Quit.
    ContextMenu Menu(params MenuItem[] items)
    {
        var m = new ContextMenu { FontFamily = FontFamily, FontSize = 14 }; // else it inherits the icon font from the ⋮ button
        foreach (var i in items) m.Items.Add(i);
        m.Items.Add(new Separator());
        m.Items.Add(Item(IconQuit, "Quit", Quit, "Ctrl+Q"));
        return m;
    }

    static MenuItem Item(string icon, string label, Action a, string keys = "")
    {
        var i = new MenuItem { Header = label, InputGestureText = keys, Icon = MenuIcon(icon) };
        i.Click += (_, _) => a();
        return i;
    }

    // Built-in editing command (label, shortcut and enabled state come with it), aimed at a text field.
    static MenuItem Item(string icon, RoutedUICommand cmd, IInputElement target) => new() { Command = cmd, CommandTarget = target, Icon = MenuIcon(icon) };

    static TextBlock MenuIcon(string icon) => new() { Text = icon, FontFamily = IconFont, FontSize = 14 };

    ContextMenu TextMenu(TextBoxBase box) =>
        Menu(Item(IconCut, ApplicationCommands.Cut, box), Item(IconCopy, ApplicationCommands.Copy, box), Item(IconPaste, ApplicationCommands.Paste, box));

    // In the editor the menu is always for the open note; in the list it's for that card.
    ContextMenu MenuFor(Note n) => Menu(
        Item(n.Pinned ? IconUnpin : IconPin, n.Pinned ? "Unpin" : "Pin", () => Toggle(n, pin: true)),
        Item(n.Public ? IconLock : IconCloud, n.Public ? "Make private" : "Make public", () => Toggle(n, pin: false)),
        Item(IconCopy, "Copy", () => Copy(cur != null ? Snapshot() : n)),
        Item(IconDelete, "Delete", () => Delete(n)));

    void Toggle(Note n, bool pin)
    {
        if (cur == null) { store.Put(pin ? n with { Pinned = !n.Pinned } : n with { Public = !n.Public }); return; }
        if (pin) cur.Pinned = !cur.Pinned;
        else cur.Public = !cur.Public;
        Paint();
        Save();
    }

    void Delete(Note n)
    {
        store.Remove(n.Id);
        if (cur == null) return;
        saveTimer.Stop();
        cur = null;
        ShowHistory();
    }

    void Copy(Note n)
    {
        var d = new DataObject();
        var text = n.Text.Replace(Mark, "");
        if (text.Trim().Length > 0) d.SetText(text.Replace("\n", "\r\n"));
        // ponytail: the clipboard holds one picture, so only the note's first image goes along with the text.
        if (n.Images.Count > 0 && Png(n.Images[0]) is { } b)
        {
            d.SetImage(b);
            d.SetData("PNG", new MemoryStream(Convert.FromBase64String(n.Images[0])));
        }
        try { Clipboard.SetDataObject(d, true); Notify("Copied"); }
        catch (ExternalException) { Notify("Clipboard is busy, try again"); }
    }

    void Notify(string msg)
    {
        ToastText.Text = msg;
        Toast.Visibility = Visibility.Visible;
        toastTimer.Stop();
        toastTimer.Start();
    }

    void Back(object s, RoutedEventArgs e) => ShowHistory();
    void NewClick(object s, RoutedEventArgs e) => NewNote();
    void TogglePublic(object s, RoutedEventArgs e) => Toggle(cur!, pin: false);
    void DragWindow(object s, MouseButtonEventArgs e) => DragMove();
    void More(object s, RoutedEventArgs e)
    {
        var m = MenuFor(cur!);
        m.PlacementTarget = (UIElement)s;
        m.Placement = PlacementMode.Custom; // right-aligned under ⋮ so it stays inside the window
        m.CustomPopupPlacementCallback = (popup, target, _) =>
            [new CustomPopupPlacement(new Point(target.Width - popup.Width, target.Height), PopupPrimaryAxis.Horizontal)];
        m.IsOpen = true;
    }

    // --- helpers -------------------------------------------------------------------------------------------

    static Visibility Vis(bool show) => show ? Visibility.Visible : Visibility.Collapsed;

    // Theme brushes are looked up by name so light/dark switches apply live.
    static T Themed<T>(T el, string brush) where T : FrameworkElement
    {
        el.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return el;
    }

    static TextBlock Glyph(string icon, string tip) => Themed(new TextBlock
    {
        Text = icon, FontFamily = IconFont, FontSize = 14, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = tip,
    }, "AccentTextFillColorPrimaryBrush");

    // The app icon, recoloured light on a dark taskbar where its grey would disappear.
    static System.Drawing.Icon TrayIcon()
    {
        using var ico = new System.Drawing.Icon(Application.GetResourceStream(new Uri("pack://application:,,,/JackTheClipper;component/app.ico")).Stream,
            System.Windows.Forms.SystemInformation.SmallIconSize);
        var darkTaskbar = Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 1) is 0;
        if (!darkTaskbar) return (System.Drawing.Icon)ico.Clone();
        using var bmp = ico.ToBitmap();
        for (var x = 0; x < bmp.Width; x++)
            for (var y = 0; y < bmp.Height; y++)
                bmp.SetPixel(x, y, System.Drawing.Color.FromArgb(bmp.GetPixel(x, y).A, 235, 235, 235));
        return System.Drawing.Icon.FromHandle(bmp.GetHicon());
    }
}
