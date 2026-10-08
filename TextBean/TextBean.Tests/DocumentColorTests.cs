using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TextBean.ViewModels;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 문서 색 — 저장 색 ↔ 어둡게에서 보이는 색 (D-162). 테마는 정적이라 바꾼 시험은 반드시 밝게로 되돌린다.
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class DocumentColorTests
{
    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static void Themed(bool dark, Action body)
    {
        AppTheme.Select(dark ? ThemeChoices.DarkId : ThemeChoices.DefaultId);
        try { body(); }
        finally { AppTheme.Select(ThemeChoices.DefaultId); }
    }

    /// 화면 밖 창에 띄운 본문 — 저장 때 본문 글자색이 감싸는 요소에 박히는 것까지 실제와 같게. 실행취소도 창 안에서만 된다 [실측].
    private static RichTextBox Host(FlowDocument document, bool dark, bool attach = false)
    {
        var box = new RichTextBox { Foreground = Frozen(dark ? DocumentColors.BodyDark : DocumentColors.BodyLight) };
        if (attach) RichBodyBehavior.SetAttach(box, true);
        box.Document = document;
        var window = new Window { Left = -20000, Top = -20000, Width = 500, Height = 300, ShowActivated = false, ShowInTaskbar = false, Content = box };
        window.Show();
        Pump();
        return box;
    }

    private static void Close(RichTextBox box) => Window.GetWindow(box)?.Close();

    /// 저장 색으로 칠한 표본: 글자색 6 · 형광펜 4 · 굵게 · 그림 · 표(칸 선).
    private static FlowDocument Sample()
    {
        var paragraph = new Paragraph();
        // 글자색 「빨강」과 형광펜 「빨강」은 이름이 같다(D-178) — 표본 글자에 목록 앞말을 붙여 찾는다
        foreach (var pair in DocumentColors.Text) paragraph.Inlines.Add(new Run(TextSample(pair)) { Foreground = Frozen(pair.Stored) });
        foreach (var pair in DocumentColors.Highlight) paragraph.Inlines.Add(new Run(HighlightSample(pair)) { Background = Frozen(pair.Stored) });
        paragraph.Inlines.Add(new Run("굵게") { FontWeight = FontWeights.Bold });
        var pixels = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, new byte[16], 8);
        paragraph.Inlines.Add(new InlineUIContainer(new Image { Source = pixels, Width = 2 }));

        var table = new Table();
        var group = new TableRowGroup();
        var row = new TableRow();
        row.Cells.Add(new TableCell(new Paragraph(new Run("칸"))) { BorderBrush = Frozen(DocumentColors.TableLine.Stored), BorderThickness = new Thickness(1) });
        group.Rows.Add(row);
        table.RowGroups.Add(group);

        var document = new FlowDocument(paragraph);
        document.Blocks.Add(table);
        return document;
    }

    private static IEnumerable<DependencyObject> Elements(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var deeper in Elements(child)) yield return deeper;
        }
    }

    private static Run RunOf(FlowDocument document, string text) => Elements(document).OfType<Run>().First(r => r.Text == text);

    private static string TextSample(DocumentColors.Pair pair) => "글자 · " + pair.Name;

    private static string HighlightSample(DocumentColors.Pair pair) => "형광 · " + pair.Name;

    private static Color? Local(DependencyObject element, DependencyProperty property)
        => (element.ReadLocalValue(property) as SolidColorBrush)?.Color;

    private static Color? CellLine(FlowDocument document) => Local(Elements(document).OfType<TableCell>().Single(), TableCell.BorderBrushProperty);

    /// 문서 안 글자색 · 형광펜 · 칸 선을 저장 색(dark=false) 또는 보이는 색(dark=true)으로 가졌는가
    private static void AssertColors(FlowDocument document, bool dark)
    {
        foreach (var pair in DocumentColors.Text) Assert.Equal(DocumentColors.Shown(pair, dark), Local(RunOf(document, TextSample(pair)), TextElement.ForegroundProperty));
        foreach (var pair in DocumentColors.Highlight) Assert.Equal(DocumentColors.Shown(pair, dark), Local(RunOf(document, HighlightSample(pair)), TextElement.BackgroundProperty));
        Assert.Equal(DocumentColors.Shown(DocumentColors.TableLine, dark), CellLine(document));
    }

    private static void AssertNoBakedForeground(FlowDocument document)
        => Assert.DoesNotContain(Elements(document).Append(document), e => Local(e, TextElement.ForegroundProperty) is { } c && DocumentColors.Baked.Contains(c));

    private static byte[] SaveAll(RichTextBox box) => RichTextMap.Save(new TextRange(box.Document.ContentStart, box.Document.ContentEnd));

    // ── 짝 목록 ─────────────────────────────────────────────────────────────

    /// 저장 바이트를 색 값으로 찾아 바꾼다 — 값이 겹치면 다른 색이 잘못 바뀐다
    [Fact]
    public void 짝_값은_서로_겹치지_않고_서식_단추_목록과_같다()
    {
        var all = DocumentColors.All.SelectMany(p => new[] { p.Stored, p.Dark }).Concat(DocumentColors.Baked).ToList();
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(DocumentColors.Text.Select(p => p.Name), RichFormat.TextColors.Keys);
        Assert.Equal(DocumentColors.Highlight.Select(p => p.Name), RichFormat.HighlightColors.Keys);
        Assert.Equal(Color.FromRgb(0xE2, 0x4B, 0x4A), RichFormat.TextColors["빨강"]);   // 저장 색은 지금까지의 값 그대로
    }

    /// D-178 (사용자 요청 10-08) — 어둡게에서 빨강이 빨강으로 보이고, 형광펜에도 빨강이 있다
    [Fact]
    public void 빨강_형광펜이_목록_끝에_있고_빨강_글자는_어둡게에서_또렷한_빨강이다()
    {
        Assert.Equal(["노랑", "연두", "하늘", "분홍", "빨강"], DocumentColors.Highlight.Select(p => p.Name));
        Assert.Equal((Color.FromRgb(0xFF, 0xA8, 0xA8), Color.FromRgb(0xA3, 0x2D, 0x2D)), (DocumentColors.FindHighlight("빨강")!.Stored, DocumentColors.FindHighlight("빨강")!.Dark));
        Assert.Equal((Color.FromRgb(0xE2, 0x4B, 0x4A), Color.FromRgb(0xFF, 0x6B, 0x6B)), (DocumentColors.FindText("빨강")!.Stored, DocumentColors.FindText("빨강")!.Dark));
    }

    [Fact]
    public void 어둡게에서_칠한_빨강_형광펜은_밝은_빨강으로_저장된다() => Run(() =>
    {
        byte[] saved = [];
        Themed(dark: true, () =>
        {
            var box = Host(new FlowDocument(new Paragraph(new Run("비밀번호 바꿈"))), dark: true, attach: true);
            var map = RichTextMap.Build(box.Document);
            box.Selection.Select(map.PointerAt(0)!, map.PointerAt(4)!);

            RichFormat.SetHighlight.Execute("빨강", box);

            Assert.Equal(Color.FromRgb(0xA3, 0x2D, 0x2D), ((SolidColorBrush)box.Selection.GetPropertyValue(TextElement.BackgroundProperty)).Color);
            saved = SaveAll(box);
            Close(box);
        });

        var back = RichTextMap.Load(saved);
        var at = RichTextMap.Build(back);
        Assert.Equal(Color.FromRgb(0xFF, 0xA8, 0xA8), ((SolidColorBrush)new TextRange(at.PointerAt(0)!, at.PointerAt(1)!).GetPropertyValue(TextElement.BackgroundProperty)).Color);
    });

    /// 실제 창의 「형광펜 ▾」 목록 — 없음 + 5칸, 마지막이 빨강. 목록은 열지 않고 내용만 본다(D-082)
    [Fact]
    public void 형광펜_목록에_빨강_칸이_있다() => Run(() =>
    {
        using var scene = TabScene.Open(["문서"]);
        scene.Shell.ActiveTab = scene.Tab(0);
        scene.Window.UpdateLayout();
        var host = FindDescendant<FrameworkElement>(scene.Window, e => e.FindName("HighlightPopup") is not null)!;
        var popup = (System.Windows.Controls.Primitives.Popup)host.FindName("HighlightPopup");
        var buttons = LogicalTreeHelper.GetChildren(((Border)popup.Child).Child).OfType<Button>().ToList();

        Assert.Equal(["", "노랑", "연두", "하늘", "분홍", "빨강"], buttons.Select(b => b.CommandParameter));
        Assert.All(buttons, b => Assert.Same(RichFormat.SetHighlight, b.Command));
    });

    // ── 열기 · 저장 왕복 ────────────────────────────────────────────────────

    [Fact]
    public void 어둡게에서_열면_보이는_색이고_저장하면_원래_저장_색으로_돌아온다() => Run(() =>
    {
        var light = Host(Sample(), dark: false);
        var original = SaveAll(light);
        Close(light);

        byte[] savedInDark = [];
        Themed(dark: true, () =>
        {
            var opened = RichTextMap.Load(original);
            AssertColors(opened, dark: true);
            AssertNoBakedForeground(opened);                                   // 밝게에서 박힌 검정은 지워져 밝은 본문색을 따른다

            var box = Host(opened, dark: true);
            savedInDark = SaveAll(box);
            Close(box);
        });

        // 저장된 바이트 자체가 저장 색이다 — 열 때 맞추기와 상관없이(옛 앱 · 되돌린 앱도 그대로 연다). 맞추지 않고 그대로 풀어 본다
        var raw = new FlowDocument();
        using (var stream = new MemoryStream(savedInDark)) new TextRange(raw.ContentStart, raw.ContentEnd).Load(stream, DataFormats.XamlPackage);
        AssertColors(raw, dark: false);

        var back = RichTextMap.Load(savedInDark);
        AssertColors(back, dark: false);
        AssertNoBakedForeground(back);                                         // 어둡게 본문색(#E6E6E6)이 남아 흰 글자가 되지 않는다
        Assert.Equal(FontWeights.Bold, RunOf(back, "굵게").FontWeight);
        Assert.Single(Elements(back).OfType<Image>());                        // 그림 부분은 그대로 옮겨졌다
        Assert.Equal(RichTextMap.Build(RichTextMap.Load(original)).Text, RichTextMap.Build(back).Text);
    });

    /// 계획 검토 R-1 — 목록 밖 색은 사용자 색이다. 지우면 색 목록을 바꿀 때 옛 문서의 색이 사라진다
    [Fact]
    public void 박힌_기본색만_지우고_목록_밖_색은_남긴다() => Run(() =>
    {
        var other = Color.FromRgb(0x12, 0x34, 0x56);
        var paragraph = new Paragraph(new Run("남는 색") { Foreground = Frozen(other) });
        foreach (var baked in DocumentColors.Baked) paragraph.Inlines.Add(new Run(baked.ToString()) { Foreground = Frozen(baked) });
        var box = Host(new FlowDocument(paragraph), dark: false);
        var saved = SaveAll(box);
        Close(box);

        foreach (var dark in new[] { false, true })
        {
            Themed(dark, () =>
            {
                var opened = RichTextMap.Load(saved);
                AssertNoBakedForeground(opened);
                Assert.Equal(other, Local(RunOf(opened, "남는 색"), TextElement.ForegroundProperty));
            });
        }
    });

    // ── 복사 ───────────────────────────────────────────────────────────────

    /// 다른 앱(밝은 바탕)에 붙여도 지금과 같게 — 어둡게 본문색은 검정으로
    [Fact]
    public void 어둡게에서_복사한_RTF_는_저장_색이고_본문색은_검정이다() => Run(() => Themed(dark: true, () =>
    {
        var red = DocumentColors.FindText("빨강")!;
        var box = Host(new FlowDocument(new Paragraph(new Run("빨강") { Foreground = Frozen(red.Dark) }) { Inlines = { new Run(" 본문") } }), dark: true);

        var rtf = RichTextMap.SaveRtf(new TextRange(box.Document.ContentStart, box.Document.ContentEnd));
        Close(box);

        Assert.Contains(@"\red226\green75\blue74;", rtf);
        Assert.DoesNotContain(@"\red240\green149\blue149;", rtf);
        Assert.DoesNotContain(@"\red230\green230\blue230;", rtf);
    }));

    // ── 붙이기 · 칠하기 ────────────────────────────────────────────────────

    private static DataObject Copied(FlowDocument source)
        => ClipboardService.BuildDataObject(RichBodyBehavior.PayloadOf(new TextRange(source.ContentStart, source.ContentEnd)));

    private static DataObjectPastingEventArgs Paste(RichTextBox box, DataObject data)
    {
        var args = new DataObjectPastingEventArgs(data, isDragDrop: false, DataFormats.XamlPackage) { RoutedEvent = DataObject.PastingEvent };
        box.RaiseEvent(args);
        return args;
    }

    [Fact]
    public void 어둡게에서_앱_안_서식을_붙이면_보이는_색으로_들어오고_실행취소_한_번이다() => Run(() => Themed(dark: true, () =>
    {
        var blue = DocumentColors.FindText("파랑")!;
        var yellow = DocumentColors.FindHighlight("노랑")!;
        var source = new FlowDocument(new Paragraph(new Run("파랑") { Foreground = Frozen(blue.Stored) })
        {
            Inlines = { new Run("노랑") { Background = Frozen(yellow.Stored) }, new Run("검정") { Foreground = Frozen(DocumentColors.BodyLight) } }
        });
        var data = Copied(source);
        var box = Host(new FlowDocument(new Paragraph()), dark: true, attach: true);

        Assert.True(Paste(box, data).CommandCancelled);

        Assert.Equal(blue.Dark, Local(RunOf(box.Document, "파랑"), TextElement.ForegroundProperty));
        Assert.Equal(yellow.Dark, Local(RunOf(box.Document, "노랑"), TextElement.BackgroundProperty));
        AssertNoBakedForeground(box.Document);                                 // 밝게에서 칠한 「기본색」 검정은 어두운 바탕에서 사라지지 않게 지운다
        box.Undo();
        Assert.Equal("", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.Trim());
        Close(box);
    }));

    [Fact]
    public void 어둡게에서_표_칸에_붙여도_보이는_색이다() => Run(() => Themed(dark: true, () =>
    {
        // 표째 복사해 칸에 붙인다 — 칸 덮어쓰기 길(RichTable.Paste, D-137). 글자만 복사하면 일반 붙여넣기 길로 간다
        var red = DocumentColors.FindText("빨강")!;
        var source = RichTable.Create(1, 2);
        ((Paragraph)source.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock).Inlines.Add(new Run("빨강") { Foreground = Frozen(red.Stored) });
        ((Paragraph)source.RowGroups[0].Rows[0].Cells[1].Blocks.FirstBlock).Inlines.Add(new Run("둘째"));
        var sourceBox = Host(new FlowDocument(source), dark: false);
        var data = ClipboardService.BuildDataObject(RichBodyBehavior.PayloadOf(
            new TextRange(source.RowGroups[0].Rows[0].Cells[0].ContentStart, source.RowGroups[0].Rows[0].Cells[1].ContentEnd)));
        Close(sourceBox);

        var target = RichTable.Create(1, 2);
        var box = Host(new FlowDocument(target), dark: true, attach: true);
        Assert.Equal(DocumentColors.TableLine.Dark, Local(target.RowGroups[0].Rows[0].Cells[0], TableCell.BorderBrushProperty));   // 새 표의 칸 선
        box.CaretPosition = target.RowGroups[0].Rows[0].Cells[1].ContentStart.GetInsertionPosition(LogicalDirection.Forward);

        Assert.True(Paste(box, data).CommandCancelled);

        Assert.Single(Elements(box.Document).OfType<Table>());                 // 표 안에 표가 아니라 칸을 덮어썼다
        Assert.Equal(red.Dark, Local(RunOf(box.Document, "빨강"), TextElement.ForegroundProperty));
        Close(box);
    }));

    [Fact]
    public void 어둡게에서_칠한_색은_저장_색으로_저장되고_기본색은_다시_열면_테마를_따른다() => Run(() =>
    {
        byte[] saved = [];
        Themed(dark: true, () =>
        {
            var box = Host(new FlowDocument(new Paragraph(new Run("파랑노랑기본"))), dark: true, attach: true);
            void Select(int from, int to)
            {
                var map = RichTextMap.Build(box.Document);
                box.Selection.Select(map.PointerAt(from)!, map.PointerAt(to)!);
            }

            Select(0, 2); RichFormat.SetForeground.Execute("파랑", box);
            Select(2, 4); RichFormat.SetHighlight.Execute("노랑", box);
            Select(4, 6); RichFormat.SetForeground.Execute("빨강", box); RichFormat.SetForeground.Execute("", box);

            var at = RichTextMap.Build(box.Document);
            Assert.Equal(DocumentColors.FindText("파랑")!.Dark,
                ((SolidColorBrush)new TextRange(at.PointerAt(0)!, at.PointerAt(1)!).GetPropertyValue(TextElement.ForegroundProperty)).Color);
            saved = SaveAll(box);
            Close(box);
        });

        var back = RichTextMap.Load(saved);
        var map = RichTextMap.Build(back);
        Color? Fore(int i) => new TextRange(map.PointerAt(i)!, map.PointerAt(i + 1)!).GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush b ? b.Color : null;
        Color? Back(int i) => new TextRange(map.PointerAt(i)!, map.PointerAt(i + 1)!).GetPropertyValue(TextElement.BackgroundProperty) is SolidColorBrush b ? b.Color : null;
        Assert.Equal(DocumentColors.FindText("파랑")!.Stored, Fore(0));
        Assert.Equal(DocumentColors.FindHighlight("노랑")!.Stored, Back(2));
        AssertNoBakedForeground(back);                                         // 「기본색」(본문색)은 박힌 기본색 — 다시 열면 지워진다
    });
}
