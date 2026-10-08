using System.IO;
using System.IO.Packaging;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace TextBean.Views.Platform;

/// <summary>
/// 문서 색 — 저장 색 ↔ 어둡게에서 보이는 색 (D-162, 사용자 판정 안 2).
/// <para>
/// 문서에는 늘 <b>저장 색</b>(밝은 바탕용 · 지금까지의 값)이 담긴다. 어둡게일 때만 열 때 · 붙일 때 보이는 색으로 바꾸고,
/// 저장 · 복사 때는 나온 바이트 안의 색 값만 되돌린다 — 본문(실행취소 · 고쳐짐)은 건드리지 않는다.
/// 문서를 다시 담아 고치는 방식은 빈 문서 기본값(Georgia 16)이 박혀 쓰지 않는다 (LL-096).
/// </para>
/// 어둡게 값은 서로 · 저장 색 · 박힌 기본색과 겹치지 않는다 — 바이트를 값으로 찾아 바꾸기 때문이다(시험이 막는다).
/// </summary>
public static partial class DocumentColors
{
    public sealed record Pair(string Name, Color Stored, Color Dark);

    /// 글자색 6 (D-127 목록 · 이름은 명령 매개변수). 어둡게 값은 어두운 바탕(#1E1E1E)에서 대비 6.0 ~ 8.2 [계산].
    /// 빨강 어둡게 값은 #F09595(연분홍처럼 보임 — 「빨강이 없다」 사용자 지적)에서 또렷한 빨강으로 (D-178).
    public static IReadOnlyList<Pair> Text { get; } =
    [
        new("빨강", Rgb(0xE2, 0x4B, 0x4A), Rgb(0xFF, 0x6B, 0x6B)),
        new("주황", Rgb(0xD8, 0x5A, 0x30), Rgb(0xF0, 0x99, 0x7B)),
        new("초록", Rgb(0x3B, 0x6D, 0x11), Rgb(0x97, 0xC4, 0x59)),
        new("파랑", Rgb(0x18, 0x5F, 0xA5), Rgb(0x85, 0xB7, 0xEB)),
        new("보라", Rgb(0x53, 0x4A, 0xB7), Rgb(0xAF, 0xA9, 0xEC)),
        new("회색", Rgb(0x88, 0x87, 0x80), Rgb(0xB4, 0xB2, 0xA9)),
    ];

    /// 형광펜 5. 어둡게 값 위의 밝은 본문 글자 대비 5.7 ~ 8.3 [계산].
    public static IReadOnlyList<Pair> Highlight { get; } =
    [
        new("노랑", Rgb(0xFA, 0xC7, 0x75), Rgb(0x63, 0x38, 0x06)),
        new("연두", Rgb(0xC0, 0xDD, 0x97), Rgb(0x27, 0x50, 0x0A)),
        new("하늘", Rgb(0xB5, 0xD4, 0xF4), Rgb(0x0C, 0x44, 0x7C)),
        new("분홍", Rgb(0xF4, 0xC0, 0xD1), Rgb(0x72, 0x24, 0x3E)),
        // D-178 (사용자 요청 10-08). 이름이 글자색 「빨강」과 같다 — 두 목록은 따로 쓴다(명령도 따로). 어둡게 값은 분홍(#72243E)과 구별되게 밝은 빨강
        new("빨강", Rgb(0xFF, 0xA8, 0xA8), Rgb(0xA3, 0x2D, 0x2D)),
    ];

    /// 새 표의 칸 선 (D-134). 문서에 저장된다.
    public static Pair TableLine { get; } = new("표 칸 선", Rgb(0xD3, 0xD1, 0xC7), Rgb(0x5F, 0x5E, 0x5A));

    /// 본문 상자의 기본 글자색. 저장하면 감싸는 요소에 박힌다 [실측] — 어둡게 값은 저장 때 검정으로 되돌린다.
    public static Color BodyLight { get; } = Rgb(0x00, 0x00, 0x00);

    public static Color BodyDark { get; } = Rgb(0xE6, 0xE6, 0xE6);

    /// <summary>
    /// 저장 때 박힌 본문 기본색 — 열 때 · 붙일 때 지워 테마를 따라가게 한다 (리서치 3-1).
    /// #E4000000 · #FFFFFFFF 는 Fluent 밝게 · 어둡게의 기본 글자색 [실측]. 「기본색」 단추도 본문색을 칠한다 — 같이 지워진다.
    /// 목록 밖 색은 사용자가 고른 색이라 그대로 둔다 — 색 목록을 바꿔도 옛 문서의 색은 남는다 (계획 검토 R-1).
    /// </summary>
    public static IReadOnlyList<Color> Baked { get; } =
        [BodyLight, Color.FromArgb(0xE4, 0x00, 0x00, 0x00), Rgb(0xFF, 0xFF, 0xFF), BodyDark];

    public static IEnumerable<Pair> All => Text.Concat(Highlight).Append(TableLine);

    public static Color Shown(Pair pair, bool dark) => dark ? pair.Dark : pair.Stored;

    public static Pair? FindText(string name) => Text.FirstOrDefault(p => p.Name == name);

    public static Pair? FindHighlight(string name) => Highlight.FirstOrDefault(p => p.Name == name);

    /// 연 문서를 지금 테마에 맞춘다. 본문에 붙이기 전에 부른다 — 붙인 뒤 고치면 실행취소 기록에 끼어든다 (ClearFonts 와 같다).
    public static void Prepare(FlowDocument document, bool dark)
    {
        Fix(document, dark);
        Walk(document);

        void Walk(DependencyObject parent)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<TextElement>())
            {
                Fix(child, dark);
                Walk(child);
            }
        }
    }

    /// 범위 안에 시작하는 글자 요소만 맞춘다 — 막 붙인 조각에, 붙이기와 같은 변경 안에서 쓴다 (ClearFonts 범위판과 같다).
    public static void Prepare(TextPointer start, TextPointer end, bool dark)
    {
        for (var at = start; at is not null && at.CompareTo(end) < 0; at = at.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (at.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.ElementStart
                && at.GetAdjacentElement(LogicalDirection.Forward) is TextElement element)
                Fix(element, dark);
        }
    }

    private static void Fix(DependencyObject element, bool dark)
    {
        if (element.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush foreground)
        {
            if (Baked.Contains(foreground.Color)) element.ClearValue(TextElement.ForegroundProperty);
            else Swap(element, TextElement.ForegroundProperty, foreground.Color, Text, dark);
        }

        if (element.ReadLocalValue(TextElement.BackgroundProperty) is SolidColorBrush background)
            Swap(element, TextElement.BackgroundProperty, background.Color, Highlight, dark);

        var border = element switch
        {
            TableCell => TableCell.BorderBrushProperty,
            Block => Block.BorderBrushProperty,
            _ => null,
        };
        if (border is not null && element.ReadLocalValue(border) is SolidColorBrush line)
            Swap(element, border, line.Color, [TableLine], dark);
    }

    /// 다른 쪽 테마의 값이면 이쪽 값으로. 목록에 없는 색은 그대로.
    private static void Swap(DependencyObject element, DependencyProperty property, Color color, IReadOnlyList<Pair> pairs, bool dark)
    {
        foreach (var pair in pairs)
        {
            var other = dark ? pair.Stored : pair.Dark;
            if (color != other) continue;
            element.SetValue(property, Frozen(Shown(pair, dark)));
            return;
        }
    }

    /// <summary>
    /// 어둡게에서 저장한 서식 바이트(XamlPackage)의 색을 저장 색으로 되돌린다. 본문은 건드리지 않는다.
    /// 패키지 안 XAML 부분의 <c>="#AARRGGBB"</c> 값만 바꿔 쓰고, 그림 등 다른 부분은 그대로 둔다.
    /// 못 읽으면 던진다 — 저장은 실패로 끝나고 원본은 덮이지 않는다(저장 실패 길, D-005 원칙).
    /// </summary>
    public static byte[] ToStored(byte[] package)
    {
        using var stream = new MemoryStream();
        stream.Write(package);
        using (var opened = Package.Open(stream, FileMode.Open, FileAccess.ReadWrite))
        {
            foreach (var part in opened.GetParts().Where(p => p.Uri.OriginalString.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                byte[] raw;
                using (var read = part.GetStream(FileMode.Open, FileAccess.Read))
                using (var copy = new MemoryStream())
                {
                    read.CopyTo(copy);
                    raw = copy.ToArray();
                }

                var bom = raw.AsSpan().StartsWith(Utf8Bom);
                var xaml = Encoding.UTF8.GetString(raw, bom ? Utf8Bom.Length : 0, raw.Length - (bom ? Utf8Bom.Length : 0));
                var fixedXaml = XamlColor().Replace(xaml, m => XamlStored.TryGetValue(m.Groups[1].Value.ToUpperInvariant(), out var stored)
                    ? $"=\"#{stored}\"" : m.Value);
                if (fixedXaml == xaml) continue;

                using var write = part.GetStream(FileMode.Create, FileAccess.Write);
                if (bom) write.Write(Utf8Bom);
                write.Write(Encoding.UTF8.GetBytes(fixedXaml));
            }
        }

        return stream.ToArray();
    }

    /// 어둡게에서 만든 복사 RTF 의 색 표를 저장 색으로 되돌린다 — 다른 앱(밝은 바탕)에 붙여도 지금과 같게. 본문색은 검정.
    public static string ToStoredRtf(string rtf)
        => RtfColor().Replace(rtf, m =>
        {
            var color = Rgb(byte.Parse(m.Groups[1].Value), byte.Parse(m.Groups[2].Value), byte.Parse(m.Groups[3].Value));
            return RtfStored.TryGetValue(color, out var stored) ? $@"\red{stored.R}\green{stored.G}\blue{stored.B};" : m.Value;
        });

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// 어둡게 값 → 저장 값. 본문 어둡게 색은 검정으로.
    private static readonly Dictionary<Color, Color> RtfStored =
        All.Select(p => (p.Dark, p.Stored)).Append((BodyDark, BodyLight)).ToDictionary(x => x.Item1, x => x.Item2);

    private static readonly Dictionary<string, string> XamlStored =
        RtfStored.ToDictionary(kv => Hex(kv.Key), kv => Hex(kv.Value));

    private static string Hex(Color c) => $"{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    [GeneratedRegex(@"=""#([0-9A-Fa-f]{8})""")]
    private static partial Regex XamlColor();

    [GeneratedRegex(@"\\red(\d{1,3})\\green(\d{1,3})\\blue(\d{1,3});")]
    private static partial Regex RtfColor();

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
