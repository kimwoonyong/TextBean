using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace TextBean.Views.Platform;

/// <summary>
/// 서식 6종 (D-127): 굵게 · 기울임 · 밑줄 · 취소선 · 글자색 · 형광펜, 그리고 서식 지우기.
/// 밑줄과 취소선은 같은 TextDecorations 칸을 쓴다. WPF 기본 밑줄(Ctrl+U)은 그 칸을 통째로 갈아 끼워
/// 취소선을 지운다 — 그래서 밑줄도 여기 명령으로 받는다. 굵게 · 기울임은 WPF 기본 명령을 쓴다.
/// </summary>
public static class RichFormat
{
    public static readonly RoutedUICommand ToggleUnderline = new("밑줄", nameof(ToggleUnderline), typeof(RichFormat));
    public static readonly RoutedUICommand ToggleStrikethrough = new("취소선", nameof(ToggleStrikethrough), typeof(RichFormat));

    /// 매개변수: 색 이름(Palette 키). "" 는 기본색.
    public static readonly RoutedUICommand SetForeground = new("글자색", nameof(SetForeground), typeof(RichFormat));

    /// 매개변수: 색 이름(Palette 키). "" 는 형광펜 없음.
    public static readonly RoutedUICommand SetHighlight = new("형광펜", nameof(SetHighlight), typeof(RichFormat));

    public static readonly RoutedUICommand ClearFormatting = new("서식 지우기", nameof(ClearFormatting), typeof(RichFormat));

    /// 매개변수: 크기 글자("16"). 목록(Sizes)에 있는 값만 (D-151).
    public static readonly RoutedUICommand SetFontSize = new("글자 크기", nameof(SetFontSize), typeof(RichFormat));
    public static readonly RoutedUICommand IncreaseFontSize = new("글자 크게", nameof(IncreaseFontSize), typeof(RichFormat));
    public static readonly RoutedUICommand DecreaseFontSize = new("글자 작게", nameof(DecreaseFontSize), typeof(RichFormat));

    /// 고를 수 있는 글자 크기 (D-151). 13 이 본문 크기다. WPF 기본 키우기(0.75 씩)는 너무 잘아 목록으로 옮긴다 [실측].
    public static readonly IReadOnlyList<double> Sizes = [10, 12, 13, 16, 20, 24, 32];
    public const double DefaultSize = 13;

    /// <summary>
    /// 지금 커서 · 고른 곳의 글자 크기 — 도구 모음 「13 ▾」 이 보인다. 고른 곳이 바뀌거나 글이 바뀌면 RichBodyBehavior 가 다시 적는다.
    /// 크기가 섞인 선택은 첫 글자 크기다(WPF 는 「섞임」만 알려 준다 [실측 — 시험]).
    /// </summary>
    public static readonly DependencyProperty CurrentSizeProperty =
        DependencyProperty.RegisterAttached("CurrentSize", typeof(string), typeof(RichFormat), new PropertyMetadata("13"));

    public static string GetCurrentSize(DependencyObject element) => (string)element.GetValue(CurrentSizeProperty);

    public static void SetCurrentSize(DependencyObject element, string value) => element.SetValue(CurrentSizeProperty, value);

    public static void UpdateCurrentSize(RichTextBox box)
        => SetCurrentSize(box, SizeAt(box).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// 고른 곳의 첫 글자 크기. 크기가 섞인 선택은 GetPropertyValue 가 「섞임」(UnsetValue)을 돌려줘 [실측 — 시험],
    /// 선택이 있으면 첫 글자 자리의 요소에서 직접 읽는다. 커서만 있으면 다음에 칠 크기(GetPropertyValue)다.
    /// </summary>
    private static double SizeAt(RichTextBox box)
    {
        if (!box.Selection.IsEmpty && box.Selection.Start.GetInsertionPosition(LogicalDirection.Forward).Parent is TextElement first)
            return first.FontSize;

        return box.Selection.GetPropertyValue(TextElement.FontSizeProperty) is double size ? size : DefaultSize;
    }

    /// <summary>
    /// 목록의 다음 · 이전 크기 (D-152 · D-153). 목록에 없는 크기(예: 13.75)는 그보다 큰 · 작은 첫 목록 값, 끝(32 · 10)에서는 그대로.
    /// </summary>
    public static double StepSize(double current, bool larger)
        => larger
            ? Sizes.FirstOrDefault(s => s > current + 0.001, Sizes[^1])
            : Sizes.LastOrDefault(s => s < current - 0.001, Sizes[0]);

    /// 고정 색 목록 [제안 — D-127] — 저장 색. 목록은 DocumentColors 한 곳에 있다(어둡게에서 보이는 색과 짝, D-162).
    /// 문서에는 색 값이 저장되므로 나중에 이름을 바꿔도 옛 문서는 그대로다.
    public static readonly IReadOnlyDictionary<string, Color> TextColors = DocumentColors.Text.ToDictionary(p => p.Name, p => p.Stored);

    public static readonly IReadOnlyDictionary<string, Color> HighlightColors = DocumentColors.Highlight.ToDictionary(p => p.Name, p => p.Stored);

    /// 서식 본문에 명령을 붙인다. 키: Ctrl+U(밑줄 — 기본 동작을 갈아 끼움) · Ctrl+Shift+X(취소선).
    public static void Attach(RichTextBox box)
    {
        box.CommandBindings.Add(new CommandBinding(ToggleUnderline,
            (_, _) => ToggleDecoration(box.Selection, TextDecorationLocation.Underline), CanFormat));
        box.CommandBindings.Add(new CommandBinding(ToggleStrikethrough,
            (_, _) => ToggleDecoration(box.Selection, TextDecorationLocation.Strikethrough), CanFormat));
        // 지금 테마에서 보이는 색으로 칠한다 — 저장 때 저장 색으로 담긴다 (D-162).
        // 「기본색」은 본문색을 칠한다. 본문색은 박힌 기본색 목록에 있어 다시 열면 지워지고 테마를 따른다.
        box.CommandBindings.Add(new CommandBinding(SetForeground,
            (_, e) => box.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, Brush(DocumentColors.Text, e.Parameter) ?? box.Foreground), CanFormat));
        box.CommandBindings.Add(new CommandBinding(SetHighlight,
            (_, e) => box.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, Brush(DocumentColors.Highlight, e.Parameter)), CanFormat));
        box.CommandBindings.Add(new CommandBinding(ClearFormatting,
            (_, _) => box.Selection.ClearAllProperties(), CanFormat));
        box.CommandBindings.Add(new CommandBinding(SetFontSize,
            (_, e) => { if (ParseSize(e.Parameter) is { } size) ApplySize(box, size); },
            (_, e) => e.CanExecute = !box.IsReadOnly && ParseSize(e.Parameter) is not null));
        box.CommandBindings.Add(new CommandBinding(IncreaseFontSize, (_, _) => ApplySize(box, StepSize(SizeAt(box), larger: true)), CanFormat));
        box.CommandBindings.Add(new CommandBinding(DecreaseFontSize, (_, _) => ApplySize(box, StepSize(SizeAt(box), larger: false)), CanFormat));

        box.InputBindings.Add(new KeyBinding(ToggleUnderline, Key.U, ModifierKeys.Control));
        box.InputBindings.Add(new KeyBinding(ToggleStrikethrough, Key.X, ModifierKeys.Control | ModifierKeys.Shift));
        // WPF 기본 키우기 · 줄이기(0.75 씩)를 갈아 끼운다 (D-152)
        box.InputBindings.Add(new KeyBinding(IncreaseFontSize, Key.OemCloseBrackets, ModifierKeys.Control));
        box.InputBindings.Add(new KeyBinding(DecreaseFontSize, Key.OemOpenBrackets, ModifierKeys.Control));
    }

    private static double? ParseSize(object? parameter)
        => parameter is string text && double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var size)
           && Sizes.Contains(size) ? size : null;

    /// 고른 곳에 칠한다. 고른 것이 없으면 다음에 칠 글자의 크기다(굵게와 같다). 실행취소 한 번 · 수정됨 [실측].
    private static void ApplySize(RichTextBox box, double size)
    {
        box.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, size);
        UpdateCurrentSize(box);
    }

    private static void CanFormat(object sender, CanExecuteRoutedEventArgs e)
        => e.CanExecute = sender is RichTextBox { IsReadOnly: false };

    private static SolidColorBrush? Brush(IReadOnlyList<DocumentColors.Pair> palette, object? name)
        => palette.FirstOrDefault(p => Equals(p.Name, name)) is { } pair ? Frozen(DocumentColors.Shown(pair, AppTheme.IsDark)) : null;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 선택 안의 글자 조각마다 그 줄을 켜거나 끈다. 조각 하나라도 없으면 모두 켠다(워드와 같은 규칙).
    /// 선택 전체에 한 값을 칠하면 밑줄 · 취소선이 섞인 선택에서 한쪽이 지워진다.
    /// </summary>
    public static void ToggleDecoration(TextSelection selection, TextDecorationLocation location)
    {
        if (selection.IsEmpty)
        {
            // 빈 선택은 다음에 칠 글자의 서식이다
            var current = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            selection.ApplyPropertyValue(Inline.TextDecorationsProperty, With(current, location, !Has(current, location)));
            return;
        }

        var pieces = Pieces(selection);
        var add = !pieces.All(piece => Has(Decorations(piece), location));
        foreach (var piece in pieces)
            piece.ApplyPropertyValue(Inline.TextDecorationsProperty, With(Decorations(piece), location, add));
    }

    private static TextDecorationCollection? Decorations(TextRange range)
        => range.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;

    private static bool Has(TextDecorationCollection? decorations, TextDecorationLocation location)
        => decorations?.Any(d => d.Location == location) == true;

    private static TextDecorationCollection With(TextDecorationCollection? decorations, TextDecorationLocation location, bool on)
    {
        var result = new TextDecorationCollection((decorations ?? []).Where(d => d.Location != location));
        if (on) result.Add(location == TextDecorationLocation.Underline ? TextDecorations.Underline : TextDecorations.Strikethrough);
        result.Freeze();
        return result;
    }

    /// 선택 안의 글자 조각들. 조각을 먼저 다 모은다 — 서식을 칠하면 Run 이 쪼개진다(TextPointer 는 살아 있다).
    private static List<TextRange> Pieces(TextRange selection)
    {
        var pieces = new List<TextRange>();
        for (var at = selection.Start; at is not null && at.CompareTo(selection.End) < 0; at = at.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (at.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;

            var runEnd = at.GetPositionAtOffset(at.GetTextRunLength(LogicalDirection.Forward))!;
            var end = runEnd.CompareTo(selection.End) < 0 ? runEnd : selection.End;
            pieces.Add(new TextRange(at, end));
        }
        return pieces;
    }
}
