using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TextBean.Models;
using TextBean.ViewModels;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 서식 본문 화면 (D-125 ~ D-128). 화면 밖 · 포커스 없음 · 실제 클립보드 없음 — 복사 · 붙여넣기는 이벤트를 직접 올려 본다.
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class RichBodyTests
{
    private static RichTextBox Attach(EditorViewModel vm)
    {
        var box = new RichTextBox { DataContext = vm };
        RichBodyBehavior.SetAttach(box, true);
        EditorBehavior.SetInterceptCopy(box, true);
        return box;
    }

    private static (EditorViewModel vm, string path, FakeClipboard clip) Open(TempVault vault, DocumentBody body)
    {
        var store = TestKeys.Store(vault.Root);
        var path = Path.Combine(vault.Root, "a.tbx");
        Wait(store.CreateAsync(path));
        Wait(store.SaveAsync(path, body, binding: null));

        var clip = new FakeClipboard();
        var vm = new EditorViewModel(store, clip, new FakeDialogs(), new FakeAutoSaveTimer());
        Wait(vm.LoadAsync(path));
        return (vm, path, clip);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var deeper in FindAll<T>(child)) yield return deeper;
        }
    }

    private static void Type(RichTextBox box, string text)
        => new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text = text;

    private static void Select(RichTextBox box, int from, int to)
    {
        var map = RichTextMap.Build(box.Document);
        box.Selection.Select(map.PointerAt(from)!, map.PointerAt(to)!);
    }

    // ── 글자 ↔ 위치 (D-128) ──────────────────────────────────────────────────

    [Fact]
    public void 뽑은_글자와_위치가_같은_글자를_가리킨다() => Run(() =>
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run("ab"));
        paragraph.Inlines.Add(new Bold(new Run("cd")));
        paragraph.Inlines.Add(new LineBreak());
        paragraph.Inlines.Add(new Run("ef"));
        var document = new FlowDocument(paragraph);
        document.Blocks.Add(new Paragraph());
        document.Blocks.Add(new Paragraph(new Run("gh")));

        var map = RichTextMap.Build(document);

        Assert.Equal("abcd\r\nef\r\n\r\ngh", map.Text);
        string Between(string piece)
        {
            var at = map.Text.IndexOf(piece, StringComparison.Ordinal);
            return new TextRange(map.PointerAt(at)!, map.PointerAt(at + piece.Length)!).Text;
        }
        Assert.Equal("bc", Between("bc"));                       // 굵게에 걸친 일치
        Assert.Equal("d\r\ne", Between("d\r\ne"));              // 줄바꿈에 걸친 일치
        Assert.Equal("f\r\n\r\ng", Between("f\r\n\r\ng"));      // 빈 문단
        Assert.Null(map.PointerAt(map.Text.Length + 1));
    });

    [Fact]
    public void 서식_없는_본문은_줄마다_문단이고_다시_뽑으면_같은_글자다() => Run(() =>
    {
        var document = RichTextMap.FromPlain("하나\n둘\r\n\r\n셋");

        Assert.Equal(4, document.Blocks.Count);
        Assert.Equal("하나\r\n둘\r\n\r\n셋", RichTextMap.Build(document).Text);
    });

    // ── 불러오기 · 저장 ─────────────────────────────────────────────────────

    [Fact]
    public void 불러오면_화면이_글자를_맞추고_수정됨이_아니다() => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, _, _) = Open(vault, DocumentBody.Plain("a\nb"));

        var box = Attach(vm);

        Assert.Equal("a\r\nb", vm.Text);
        Assert.False(vm.IsDirty);
        Assert.Equal(2, box.Document.Blocks.Count);
    });

    /// 새 탭은 경로가 정해지기 전에 서식 본문으로 한 번 그려질 수 있다 — .txt 로 정해지면 손을 떼야 한다
    [Fact]
    public void txt_편집기에는_서식_본문이_붙지_않는다() => Run(() =>
    {
        using var vault = new TempVault();
        var path = vault.WriteRaw("메모.txt", "평문"u8.ToArray());
        var vm = new EditorViewModel(TestKeys.Store(vault.Root), new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer());
        Wait(vm.LoadAsync(path));

        var box = Attach(vm);

        Assert.Null(vm.CaptureBody);
        Assert.Null(vm.CaptureAllForCopy);
        Assert.Null(RichBodyBehavior.GetMap(box));
        Assert.Equal("평문", vm.Text);
    });

    [Fact]
    public void 서식_6종이_저장한_뒤_다시_열어도_남는다() => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, path, _) = Open(vault, DocumentBody.Empty);
        var box = Attach(vm);
        Type(box, "굵게 기울 밑줄 취소 빨강 노랑");

        Select(box, 0, 2); EditingCommands.ToggleBold.Execute(null, box);
        Select(box, 3, 5); EditingCommands.ToggleItalic.Execute(null, box);
        Select(box, 6, 8); RichFormat.ToggleUnderline.Execute(null, box);
        Select(box, 9, 11); RichFormat.ToggleStrikethrough.Execute(null, box);
        Select(box, 12, 14); RichFormat.SetForeground.Execute("빨강", box);
        Select(box, 15, 17); RichFormat.SetHighlight.Execute("노랑", box);
        Assert.True(vm.IsDirty);
        Assert.True(Wait(vm.TrySaveAsync()));

        var again = new EditorViewModel(TestKeys.Store(vault.Root), new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer());
        Wait(again.LoadAsync(path));
        var reopened = Attach(again);
        var map = RichTextMap.Build(reopened.Document);
        object Value(int at, DependencyProperty property) => new TextRange(map.PointerAt(at)!, map.PointerAt(at + 1)!).GetPropertyValue(property);

        Assert.Equal("굵게 기울 밑줄 취소 빨강 노랑", again.Text);
        Assert.Equal(FontWeights.Bold, Value(0, TextElement.FontWeightProperty));
        Assert.Equal(FontStyles.Italic, Value(3, TextElement.FontStyleProperty));
        Assert.Contains(TextDecorations.Underline[0].Location, Locations(Value(6, Inline.TextDecorationsProperty)));
        Assert.Contains(TextDecorationLocation.Strikethrough, Locations(Value(9, Inline.TextDecorationsProperty)));
        Assert.Equal(RichFormat.TextColors["빨강"], ((SolidColorBrush)Value(12, TextElement.ForegroundProperty)).Color);
        Assert.Equal(RichFormat.HighlightColors["노랑"], ((SolidColorBrush)Value(15, TextElement.BackgroundProperty)).Color);
        Assert.Equal(FontWeights.Normal, Value(3, TextElement.FontWeightProperty));       // 칠한 곳 밖은 그대로
    });

    private static IEnumerable<TextDecorationLocation> Locations(object value)
        => value is TextDecorationCollection decorations ? decorations.Select(d => d.Location) : [];

    /// WPF 기본 밑줄은 TextDecorations 칸을 통째로 갈아 끼워 취소선을 지운다 — 그래서 밑줄도 우리 명령이다 (D-127)
    [Fact]
    public void 밑줄과_취소선은_서로를_지우지_않는다() => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, _, _) = Open(vault, DocumentBody.Empty);
        var box = Attach(vm);
        Type(box, "abcdef");

        Select(box, 0, 4); RichFormat.ToggleStrikethrough.Execute(null, box);
        Select(box, 2, 6); RichFormat.ToggleUnderline.Execute(null, box);        // 취소선에 반쯤 걸친 밑줄

        var map = RichTextMap.Build(box.Document);
        TextDecorationLocation[] At(int i)
            => [.. Locations(new TextRange(map.PointerAt(i)!, map.PointerAt(i + 1)!).GetPropertyValue(Inline.TextDecorationsProperty)).Order()];
        Assert.Equal([TextDecorationLocation.Strikethrough], At(0));
        Assert.Equal([TextDecorationLocation.Underline, TextDecorationLocation.Strikethrough], At(2));
        Assert.Equal([TextDecorationLocation.Underline], At(5));

        Select(box, 0, 6); RichFormat.ToggleStrikethrough.Execute(null, box);    // 일부만 있으면 모두 켠다
        Assert.All(Enumerable.Range(0, 6), i => Assert.Contains(TextDecorationLocation.Strikethrough, At(i)));
        RichFormat.ToggleStrikethrough.Execute(null, box);                        // 모두 있으면 모두 끈다
        Assert.All(Enumerable.Range(0, 6), i => Assert.DoesNotContain(TextDecorationLocation.Strikethrough, At(i)));
        Assert.Contains(TextDecorationLocation.Underline, At(3));                 // 밑줄은 그대로
    });

    [Fact]
    public void 읽기_전용이면_서식_명령이_듣지_않는다() => Run(() =>
    {
        var box = new RichTextBox { IsReadOnly = true };
        RichBodyBehavior.SetAttach(box, true);

        Assert.False(RichFormat.ToggleStrikethrough.CanExecute(null, box));
        Assert.False(RichFormat.SetForeground.CanExecute("빨강", box));
    });

    // ── 복사 (D-007 · D-126) ─────────────────────────────────────────────────

    private static DataObjectCopyingEventArgs RaiseCopying(RichTextBox box, bool isDragDrop = false)
    {
        var args = new DataObjectCopyingEventArgs(new DataObject("기본 복사"), isDragDrop) { RoutedEvent = DataObject.CopyingEvent };
        box.RaiseEvent(args);
        return args;
    }

    [Fact]
    public void 복사는_기본_복사를_취소하고_고른_것을_서식째_보낸다() => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, _, clip) = Open(vault, DocumentBody.Empty);
        var box = Attach(vm);
        Type(box, "앞 비밀값 뒤");
        Select(box, 2, 5);
        EditingCommands.ToggleBold.Execute(null, box);

        var args = RaiseCopying(box);

        Assert.True(args.CommandCancelled);
        Assert.Equal("비밀값", clip.CopiedPayload!.Text);
        Assert.Contains(@"\b", clip.CopiedPayload.Rtf);
        var rtf = new FlowDocument();                    // RTF 는 7비트로 담긴다 — 한글이 ? 로 깨지지 않고 되읽힌다
        using (var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(clip.CopiedPayload.Rtf!)))
            new TextRange(rtf.ContentStart, rtf.ContentEnd).Load(stream, DataFormats.Rtf);
        Assert.Equal("비밀값", new TextRange(rtf.ContentStart, rtf.ContentEnd).Text.TrimEnd());
        var pasted = new FlowDocument();
        using (var stream = new MemoryStream(clip.CopiedPayload.XamlPackage!))
            new TextRange(pasted.ContentStart, pasted.ContentEnd).Load(stream, DataFormats.XamlPackage);
        Assert.Equal(FontWeights.Bold, new TextRange(pasted.ContentStart, pasted.ContentEnd).GetPropertyValue(TextElement.FontWeightProperty));
    });

    [Fact]
    public void 고른_것이_없으면_문서_전체를_보낸다() => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, _, clip) = Open(vault, DocumentBody.Plain("첫\n둘"));
        var box = Attach(vm);

        RaiseCopying(box);

        Assert.StartsWith("첫\r\n둘", clip.CopiedPayload!.Text);
    });

    [Fact]
    public void 끌기는_취소만_하고_아무것도_보내지_않는다() => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, _, clip) = Open(vault, DocumentBody.Plain("값"));
        var box = Attach(vm);

        var args = RaiseCopying(box, isDragDrop: true);

        Assert.True(args.CommandCancelled);
        Assert.Null(clip.Copied);
    });

    /// 플래그는 DataObject 단위다 — 서식 형식이 플래그 없는 다른 DataObject 로 나가면 그 판이 Win+V 기록에 남는다
    [Fact]
    public void 서식_형식도_기록_제외_플래그와_같은_DataObject_에_담긴다() => Run(() =>
    {
        var data = ClipboardService.BuildDataObject(new ClipboardPayload("값", "{\\rtf1 x}", [1, 2, 3]));

        Assert.Equal("값", data.GetData(DataFormats.UnicodeText));
        Assert.Equal("{\\rtf1 x}", data.GetData(DataFormats.Rtf));
        Assert.True(data.GetDataPresent(DataFormats.XamlPackage));
        Assert.True(data.GetDataPresent(ClipboardService.RichMarkerFormat));
        Assert.Equal(false, data.GetData("CanIncludeInClipboardHistory"));
        Assert.Equal(false, data.GetData("CanUploadToCloudClipboard"));
        Assert.Equal(false, data.GetData("ExcludeClipboardContentFromMonitorProcessing"));

        var plain = ClipboardService.BuildDataObject(new ClipboardPayload("값"));
        Assert.False(plain.GetDataPresent(ClipboardService.RichMarkerFormat));
        Assert.False(plain.GetDataPresent(DataFormats.XamlPackage));
        Assert.Equal(false, plain.GetData("CanIncludeInClipboardHistory"));
    });

    // ── 붙여넣기 (D-126) ─────────────────────────────────────────────────────

    private static DataObjectPastingEventArgs RaisePasting(RichTextBox box, DataObject data, string format)
    {
        var args = new DataObjectPastingEventArgs(data, isDragDrop: false, format) { RoutedEvent = DataObject.PastingEvent };
        box.RaiseEvent(args);
        return args;
    }

    [Fact]
    public void 앱_안에서_복사한_서식만_서식째_받는다() => Run(() =>
    {
        var box = Attach(new EditorViewModel(TestKeys.Store(Path.GetTempPath()), new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer()));
        var source = new FlowDocument(new Paragraph(new Run("값") { FontWeight = FontWeights.Bold }));
        var ours = ClipboardService.BuildDataObject(RichBodyBehavior.PayloadOf(new TextRange(source.ContentStart, source.ContentEnd)));

        var args = RaisePasting(box, ours, DataFormats.Rtf);

        Assert.True(args.CommandCancelled);                                   // 기본 붙여넣기 대신 직접 붙였다 (D-154)
        var pasted = RichTextMap.Build(box.Document);
        Assert.Contains("값", pasted.Text);
        var at = pasted.Text.IndexOf('값');
        Assert.Equal(FontWeights.Bold, new TextRange(pasted.PointerAt(at)!, pasted.PointerAt(at + 1)!).GetPropertyValue(TextElement.FontWeightProperty));
    });

    [Fact]
    public void 밖에서_온_서식은_글자만_받는다() => Run(() =>
    {
        var box = Attach(new EditorViewModel(TestKeys.Store(Path.GetTempPath()), new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer()));
        var word = new DataObject();
        word.SetText("밖의 글");
        word.SetData(DataFormats.Rtf, "{\\rtf1\\b 밖의 글}");
        word.SetData(DataFormats.Html, "<b>밖의 글</b>");
        word.SetData(DataFormats.XamlPackage, new MemoryStream([1]));        // 다른 WPF 앱 — 표지가 없다

        var args = RaisePasting(box, word, DataFormats.Rtf);

        Assert.False(args.CommandCancelled);
        Assert.Equal(DataFormats.UnicodeText, args.FormatToApply);
        Assert.Equal([DataFormats.UnicodeText], args.DataObject.GetFormats(autoConvert: false).Where(f => f != DataFormats.Text && f != "System.String" && f != DataFormats.OemText && f != DataFormats.Locale).ToArray());
        Assert.Equal("밖의 글", args.DataObject.GetData(DataFormats.UnicodeText));
    });

    [Fact]
    public void 글자가_없는_것은_받지_않는다() => Run(() =>
    {
        var box = Attach(new EditorViewModel(TestKeys.Store(Path.GetTempPath()), new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer()));
        var picture = new DataObject();
        picture.SetData(DataFormats.FileDrop, new[] { @"C:\x.png" });

        Assert.True(RaisePasting(box, picture, DataFormats.FileDrop).CommandCancelled);
    });

    // ── 키 (D-127) ───────────────────────────────────────────────────────────

    [Fact]
    public void 서식_6종_밖의_기본_키는_막고_밑줄_취소선은_우리_명령이다() => Run(() =>
    {
        var box = new RichTextBox();
        RichBodyBehavior.SetAttach(box, true);
        ICommand? Bound(Key key, ModifierKeys modifiers)
            => box.InputBindings.OfType<KeyBinding>().FirstOrDefault(b => b.Key == key && b.Modifiers == modifiers)?.Command;

        Assert.Equal(16, RichBodyBehavior.BlockedGestures.Count);                 // 글자 크기 둘은 살렸다 (D-152)
        Assert.All(RichBodyBehavior.BlockedGestures, g => Assert.Same(ApplicationCommands.NotACommand, Bound(g.Key, g.Modifiers)));
        Assert.Same(RichFormat.ToggleUnderline, Bound(Key.U, ModifierKeys.Control));
        Assert.Same(RichFormat.ToggleStrikethrough, Bound(Key.X, ModifierKeys.Control | ModifierKeys.Shift));
        Assert.Null(Bound(Key.B, ModifierKeys.Control));          // 굵게 · 기울임은 WPF 기본 그대로
        Assert.All(RichBodyBehavior.BlockedGestures, g =>
            Assert.NotNull(ShortcutCatalog.Reject(Views.Dialogs.ShortcutsDialog.ToText(g.Key, g.Modifiers))));   // 단축키로 고를 수 없다
    });

    // ── 찾기 (D-128) ─────────────────────────────────────────────────────────

    [Fact]
    public void 찾은_자리를_서식_본문에서_고르고_사각형을_준다() => Run(() =>
    {
        using var vault = new TempVault();
        var (vm, _, _) = Open(vault, DocumentBody.Plain("첫 줄\n둘째 줄의 비밀값\n셋"));
        var box = Attach(vm);
        SearchHighlightBehavior.SetShowMatches(box, true);
        var window = new Window
        {
            Left = -20000, Top = -20000, Width = 500, Height = 300, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = box
        };
        window.Show();
        try
        {
            vm.SetSearch("비밀값");
            Assert.True(vm.MoveToNextMatch());

            Assert.Equal("비밀값", box.Selection.Text);
            var rect = MatchHighlightAdorner.RectAt(box, vm.Matches[0]);
            Assert.False(rect.IsEmpty);
            Assert.True(rect.Y > MatchHighlightAdorner.RectAt(box, 0).Y);          // 둘째 줄
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void 탭_본문은_txt_면_TextBox_tbx_면_서식_본문이다() => Run(() =>
    {
        using var scene = TabScene.Open(["문서", "메모.txt"]);

        scene.Shell.ActiveTab = scene.Tab(0);
        scene.Window.UpdateLayout();
        Assert.IsType<RichTextBox>(scene.Window.FindActiveBodyTextBox());
        var body = (RichTextBox)scene.Window.FindActiveBodyTextBox()!;
        Type(body, "운영 DB 접속 정보 — 비밀번호 90일마다 교체 · 옛 비밀번호 폐기됨 · 새 키는 금고 2번");
        Select(body, 0, 5); EditingCommands.ToggleBold.Execute(null, body);
        Select(body, 14, 27); RichFormat.SetForeground.Execute("빨강", body);
        Select(body, 30, 40); RichFormat.ToggleStrikethrough.Execute(null, body);
        Select(body, 43, 53); RichFormat.SetHighlight.Execute("노랑", body);
        scene.Window.UpdateLayout();
        Pump(System.Windows.Threading.DispatcherPriority.Background);
        Assert.True(FindDescendant<Button>(scene.Window, b => b.Command == RichFormat.ToggleStrikethrough && b.IsVisible)!.IsEnabled);   // 도구 모음이 흐리지 않다
        Snapshot(scene.Window, "rich-body");            // TEXTBEAN_TEST_PNG 가 있을 때만

        // 표 (D-131 · D-136) — 문서 끝에 넣고 칸을 채워 그림으로 본다
        body.CaretPosition = body.Document.ContentEnd;
        RichTable.InsertTable.Execute("3x3", body);
        foreach (var word in new[] { "이름", "주소", "계정", "운영 DB", "10.0.0.5", "admin", "메일", "smtp.local", "noreply" })
        {
            body.CaretPosition.InsertTextInRun(word);
            RichTable.NextCell.Execute(null, body);
        }
        RichTable.DeleteRow.Execute(null, body);       // 마지막 칸 Tab 으로 늘어난 빈 행
        scene.Window.UpdateLayout();
        Snapshot(scene.Window, "rich-table");
        var picker = (TableSizePicker)((Border)FindAll<System.Windows.Controls.Primitives.Popup>(scene.Window).First(p => p.Name == "TablePopup").Child).Child;
        picker.Highlight((3, 4));
        picker.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        picker.Arrange(new Rect(picker.DesiredSize));
        Snapshot(picker, "table-picker");

        // 색 목록 — 팝업은 열지 않고(D-082) 안의 것만 배치해 본다. 크기 없는 색 칸은 단추 가운데로 0×0 이 됐다 [실측 — 사용자 화면]
        foreach (var popup in FindAll<System.Windows.Controls.Primitives.Popup>(scene.Window).Where(p => p.Name is "TextColorPopup" or "HighlightPopup"))
        {
            var list = (FrameworkElement)popup.Child;
            list.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            list.Arrange(new Rect(list.DesiredSize));
            var swatches = FindAll<System.Windows.Shapes.Rectangle>(list).ToList();
            Assert.NotEmpty(swatches);
            Assert.All(swatches, r => Assert.True(r.ActualWidth >= 16 && r.ActualHeight >= 16, "색 칸이 보이지 않는다"));
            Snapshot(list, "palette-" + popup.Name);
        }

        scene.Shell.ActiveTab = scene.Tab(1);
        scene.Window.UpdateLayout();
        Assert.IsType<TextBox>(scene.Window.FindActiveBodyTextBox());
        Assert.False(scene.Tab(1).ShowFormatBar);
        Assert.Null(scene.Tab(1).CaptureBody);          // 잠깐 붙었던 서식 본문이 남아 있지 않다
    });
}
