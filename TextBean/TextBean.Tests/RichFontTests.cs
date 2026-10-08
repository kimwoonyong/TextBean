using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TextBean.Models;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 본문 글꼴 D2Coding (D-148 · D-149). 화면 밖 렌더 · 클립보드 없음.
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class RichFontTests
{
    private static readonly FontFamily Old = new("Cascadia Mono, Consolas, D2Coding");
    private static readonly SolidColorBrush Highlight = Frozen(Color.FromRgb(0xFA, 0xC7, 0x75));

    /// 시험마다 다른 STA 스레드다 — 굳히지 않은 붓은 처음 쓴 스레드에 묶인다
    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [Fact]
    public void 본문_글꼴은_앱에_넣은_D2Coding_보통과_굵게다() => Run(() =>
    {
        var faces = AppFonts.Body.GetTypefaces().ToList();

        GlyphTypeface Face(FontWeight weight)
        {
            var face = faces.First(t => t.Weight == weight && t.Style == FontStyles.Normal);
            Assert.True(face.TryGetGlyphTypeface(out var glyph), $"{weight} 글꼴 파일을 못 찾았다 — 자원 등록(csproj)을 본다");
            return glyph;
        }

        var normal = Face(FontWeights.Normal);
        var bold = Face(FontWeights.Bold);
        Assert.Contains("d2coding-ver1.4.0", normal.FontUri.ToString(), StringComparison.OrdinalIgnoreCase);   // WPF 는 자원 이름을 소문자로 바꾼다
        Assert.Contains("d2codingbold-ver1.4.0", bold.FontUri.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.True(normal.CharacterToGlyphMap.ContainsKey('가') && normal.CharacterToGlyphMap.ContainsKey(' '));   // 한글 · 띄어쓰기를 한 글꼴이 그린다
    });

    [Fact]
    public void 라이선스_글도_함께_들어_있다()
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/TextBean;component/Assets/Fonts/OFL.txt"));
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!.Stream);
        Assert.Contains("SIL OPEN FONT LICENSE", reader.ReadToEnd().ToUpperInvariant());
    }

    /// 형광펜 띠의 위 · 아래 끝이 열마다 같은가. 글꼴이 섞이면 배율 125% 등에서 띄어쓰기만 1~2px 어긋났다 [실측 — 사용자 화면]
    private static HashSet<(int Top, int Bottom)> Bands(FontFamily font, double scale)
    {
        var box = new RichTextBox
        {
            Width = 300, Height = 40, FontFamily = font, FontSize = 13, BorderThickness = new Thickness(0), Padding = new Thickness(4),
            Document = new FlowDocument(new Paragraph(new Run("고양이 사진 cat 12") { Background = Highlight }) { Margin = new Thickness(0) })
        };
        var window = new Window
        {
            Left = -20000, Top = -20000, SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = box
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            int width = (int)(300 * scale), height = (int)(40 * scale);
            var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bitmap.Render(box);
            var pixels = new byte[width * height * 4];
            bitmap.CopyPixels(pixels, width * 4, 0);

            var bands = new HashSet<(int, int)>();
            for (var x = 0; x < width; x++)
            {
                int top = -1, bottom = -1;
                for (var y = 0; y < height; y++)
                {
                    var i = (y * width + x) * 4;
                    if (Math.Abs(pixels[i + 2] - 0xFA) < 8 && Math.Abs(pixels[i + 1] - 0xC7) < 8 && Math.Abs(pixels[i] - 0x75) < 8)
                    {
                        if (top < 0) top = y;
                        bottom = y;
                    }
                }
                if (top >= 0) bands.Add((top, bottom));
            }
            return bands;
        }
        finally
        {
            window.Close();
        }
    }

    /// 본문 글꼴 후보 전체 × 배율 — 후보를 늘리면 여기서 걸러진다 (D-157). 한글 · 영문을 한 글꼴이 그리지 않으면 띠가 들쭉하다.
    public static TheoryData<string, double> CandidatesAndScales()
    {
        var data = new TheoryData<string, double>();
        foreach (var choice in TextBean.ViewModels.FontChoices.All)
            foreach (var scale in new[] { 1.0, 1.25, 1.5 })
                data.Add(choice.Id, scale);
        return data;
    }

    [Theory]
    [MemberData(nameof(CandidatesAndScales))]
    public void 후보_글꼴마다_형광펜_띠는_배율마다_높이가_고르다(string id, double scale) => Run(() =>
    {
        var font = AppFonts.For(TextBean.ViewModels.FontChoices.Find(id));
        var face = font.GetTypefaces().First();
        Assert.True(face.TryGetGlyphTypeface(out var glyph), $"{id} 글꼴을 찾지 못했다");
        Assert.True(glyph.CharacterToGlyphMap.ContainsKey('가'), $"{id} 글꼴에 한글이 없다 — 대체 글꼴이 섞여 띠가 들쭉해진다");
        Assert.Single(Bands(font, scale));
    });

    // ── 문서의 글꼴 이름 (D-149) ─────────────────────────────────────────────

    private static List<TextElement> Elements(DependencyObject root)
    {
        var found = new List<TextElement>();
        void Walk(DependencyObject at)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(at).OfType<TextElement>())
            {
                found.Add(child);
                Walk(child);
            }
        }
        Walk(root);
        return found;
    }

    private static bool HasLocalFont(DependencyObject element) => element.ReadLocalValue(TextElement.FontFamilyProperty) != DependencyProperty.UnsetValue;

    /// 옛 글꼴 본문에서 서식 · 표를 넣고 저장한 것처럼 만든다
    private static (RichTextBox box, Window window) OldDocument()
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run("굵게") { FontWeight = FontWeights.Bold });
        paragraph.Inlines.Add(new Run(" 형광") { Background = Highlight });
        var document = new FlowDocument(paragraph);
        document.Blocks.Add(RichTable.Create(1, 2));
        var box = new RichTextBox { FontFamily = Old, Document = document };
        var window = new Window
        {
            Left = -20000, Top = -20000, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = box
        };
        window.Show();
        return (box, window);
    }

    [Fact]
    public void 옛_글꼴_이름이_든_문서를_열면_글꼴_이름만_지워진다() => Run(() =>
    {
        var (box, window) = OldDocument();
        byte[] saved;
        try { saved = RichTextMap.Save(new TextRange(box.Document.ContentStart, box.Document.ContentEnd)); }
        finally { window.Close(); }

        var raw = new FlowDocument();                                          // 지우기 전 — 저장된 문서에 글꼴 이름이 있다 [실측]
        using (var stream = new MemoryStream(saved)) new TextRange(raw.ContentStart, raw.ContentEnd).Load(stream, DataFormats.XamlPackage);
        Assert.Contains(Elements(raw), HasLocalFont);

        var opened = RichTextMap.Load(saved);

        Assert.DoesNotContain(Elements(opened), HasLocalFont);
        Assert.False(HasLocalFont(opened));
        var runs = Elements(opened).OfType<Run>().ToList();
        Assert.Equal(FontWeights.Bold, runs.First(r => r.Text == "굵게").FontWeight);
        Assert.NotNull(runs.First(r => r.Text == " 형광").Background);
        Assert.Single(Elements(opened).OfType<Table>());
    });

    [Fact]
    public void 앱_안_서식을_붙이면_붙은_조각에_글꼴_이름이_없다() => Run(() =>
    {
        var (source, window) = OldDocument();
        DataObject copied;
        try { copied = ClipboardService.BuildDataObject(RichBodyBehavior.PayloadOf(new TextRange(source.Document.ContentStart, source.Document.ContentEnd))); }
        finally { window.Close(); }

        var target = new RichTextBox { FontFamily = AppFonts.Body, Document = new FlowDocument(new Paragraph(new Run("ab"))) };
        RichBodyBehavior.SetAttach(target, true);
        target.CaretPosition = target.Document.ContentEnd;
        var args = new DataObjectPastingEventArgs(copied, isDragDrop: false, DataFormats.XamlPackage) { RoutedEvent = DataObject.PastingEvent };
        target.RaiseEvent(args);

        Assert.True(args.CommandCancelled);                                   // 직접 붙였다 (D-154)
        var bold = Elements(target.Document).OfType<Run>().Single(r => r.Text == "굵게");
        Assert.Equal(FontWeights.Bold, bold.FontWeight);
        Assert.DoesNotContain(Elements(target.Document), HasLocalFont);        // 옛 글꼴 이름도 상대 주소 글꼴도 없다
        Assert.Same(target.FontFamily, bold.FontFamily);                       // 실제로 본문 글꼴로 보인다
    });

    /// <summary>
    /// 사용자 재현 (D-154): 앱 안에서 기울임 「testtest」를 복사해 붙이니 다른 글꼴 · 크기로 들어갔다.
    /// 붙이기 전에 서식을 다시 담아 글꼴 이름을 지우던 방식이 빈 문서 기본값(Georgia 16 · 기울임 없음)을 박았다 [실측].
    /// 글꼴 이름이 없는지가 아니라 **실제로 보이는** 글꼴 · 크기 · 기울임을 본다.
    /// </summary>
    [Fact]
    public void 앱_안에서_복사해_붙인_글자는_원래_크기_기울임_본문_글꼴_그대로다() => Run(() =>
    {
        var paragraph = new Paragraph();
        paragraph.Inlines.Add(new Run("testtest") { FontStyle = FontStyles.Italic });
        paragraph.Inlines.Add(new Run("큰글") { FontSize = 20 });
        var source = new RichTextBox { FontFamily = AppFonts.Body, FontSize = 13, Document = new FlowDocument(paragraph) };
        var target = new RichTextBox { FontFamily = AppFonts.Body, FontSize = 13, Document = new FlowDocument(new Paragraph(new Run("앞"))) };
        RichBodyBehavior.SetAttach(target, true);
        var window = new Window
        {
            Left = -20000, Top = -20000, Width = 400, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = new StackPanel { Children = { source, target } }
        };
        window.Show();
        try
        {
            var copied = ClipboardService.BuildDataObject(RichBodyBehavior.PayloadOf(new TextRange(paragraph.ContentStart, paragraph.ContentEnd)));
            target.CaretPosition = target.Document.ContentEnd;
            target.RaiseEvent(new DataObjectPastingEventArgs(copied, isDragDrop: false, DataFormats.XamlPackage) { RoutedEvent = DataObject.PastingEvent });

            var runs = Elements(target.Document).OfType<Run>().ToList();
            var italic = runs.Single(r => r.Text == "testtest");
            var big = runs.Single(r => r.Text == "큰글");
            Assert.Equal((FontStyles.Italic, 13.0), (italic.FontStyle, italic.FontSize));
            Assert.Equal(20.0, big.FontSize);
            Assert.Same(target.FontFamily, italic.FontFamily);
            Assert.Equal(TextAlignment.Left, ((Paragraph)italic.Parent is Paragraph p ? p : (Paragraph)((Inline)italic.Parent).Parent).TextAlignment);

            target.Undo();                                                     // 붙이기 · 글꼴 이름 지우기가 한 번에 되돌아간다
            Assert.DoesNotContain(Elements(target.Document).OfType<Run>(), r => r.Text == "testtest");
        }
        finally
        {
            window.Close();
        }
    });
}
