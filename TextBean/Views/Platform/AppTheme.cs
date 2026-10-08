using System.Windows;
using System.Windows.Media;
using TextBean.ViewModels;

namespace TextBean.Views.Platform;

/// <summary>
/// 앱 테마 (D-161 · D-163). 밝게 · 어둡게 모두 Fluent(.NET 10 <c>ThemeMode</c>) 위에 앱 색 표를 얹는다.
/// <list type="bullet">
/// <item><b>창마다 건다</b> — <c>Application.ThemeMode</c> 는 앱을 만든 스레드에서만 바꿀 수 있어 시험 스레드에서 던진다 [실측].</item>
/// <item><b>InitializeComponent 앞에</b> <see cref="Use"/> 를 부른다 — 늦게 걸면 XAML 의 <c>BasedOn="{StaticResource {x:Type …}}"</c> 가
///   옛 모양 스타일을 잡아 탭 · 트리가 옛 모양으로 남는다 [실측].</item>
/// <item>테마를 바꾸면 창 안 내용이 <b>새로 만들어진다</b> — 본문도 새것이다 [실측]. 열린 본문을 먼저 거두는 것은 MainWindow 가 맡는다.</item>
/// </list>
/// 앱 색은 XAML 에서 <c>{DynamicResource 키}</c>, 코드에서 <see cref="Brush"/> 로 읽는다. 붓은 굳힌다 — 시험은 창마다 다른 STA 스레드다.
/// 정적이다 — 시험은 바꾼 뒤 반드시 기본(밝게)으로 되돌린다(AppFonts 와 같다).
/// </summary>
public static class AppTheme
{
    public static ThemeChoice Current { get; private set; } = ThemeChoices.Find(ThemeChoices.DefaultId);

    public static bool IsDark => Current.Id == ThemeChoices.DarkId;

    /// 테마가 바뀌었다. 바꾼 스레드에서 알린다 — 다른 스레드의 창은 제 디스패처로 넘겨 바꾼다.
    public static event EventHandler? Changed;

    public static void Select(string? id)
    {
        var next = ThemeChoices.Find(id);
        if (next == Current) return;

        Current = next;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// 창에 지금 테마를 걸고, 바뀌면 따라가게 한다. 창 생성자에서 <c>InitializeComponent</c> 앞에 부른다.
    /// </summary>
    public static void Use(Window window)
    {
        Apply(window);

        EventHandler changed = (_, _) =>
        {
            if (window.CheckAccess()) Apply(window);
            else window.Dispatcher.BeginInvoke(() => Apply(window));
        };
        Changed += changed;
        window.Closed += (_, _) => Changed -= changed;
    }

#pragma warning disable WPF0001   // ThemeMode 는 .NET 10 에서도 「평가 단계」 표시가 붙어 있다 [실측 — 빌드]. 바뀌면 이 한 곳을 고친다.
    private static void Apply(Window window)
    {
        window.ThemeMode = IsDark ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var merged = window.Resources.MergedDictionaries;
        foreach (var old in merged.Where(d => d.Contains(PaletteMarker)).ToList()) merged.Remove(old);
        merged.Add(Palette(IsDark));
    }

    private const string PaletteMarker = "TextBean.Palette";

    /// <summary>
    /// 앱 색 표 — 밝게 값은 지금까지의 값 그대로, 어둡게 값은 렌더로 맞췄다 [제안 — plan §1-5].
    /// 키는 Brush 로 끝낸다 — 같은 키의 Style 과 겹치면 파싱이 깨진다 (LL-005).
    /// </summary>
    private static readonly (string Key, Color Light, Color Dark)[] Colors =
    [
        ("BodyTextBrush", DocumentColors.BodyLight, DocumentColors.BodyDark),
        ("FormatBarBrush", Rgb(0xFAFAF7), Rgb(0x272727)),
        ("FormatBarLineBrush", Rgb(0xE5E3DA), Rgb(0x3A3A3A)),
        ("SeparatorBrush", Rgb(0xD3D1C7), Rgb(0x4A4944)),
        ("PopupBrush", Rgb(0xFFFFFF), Rgb(0x2B2B2B)),
        ("PopupLineBrush", Rgb(0xD3D1C7), Rgb(0x4A4944)),
        ("SurfaceBrush", Rgb(0xFFFFFF), Rgb(0x2B2B2B)),
        ("SwatchLineBrush", Rgb(0xB4B2A9), Rgb(0x6B6A64)),
        ("MutedTextBrush", Rgb(0x5F5E5A), Rgb(0xB4B2A9)),
        ("AccentBrush", Rgb(0x185FA5), Rgb(0x85B7EB)),
        ("DangerTextBrush", Rgb(0xA32D2D), Rgb(0xF09595)),
        ("ErrorTextBrush", Rgb(0xC0392B), Rgb(0xF09595)),
        ("HintTextBrush", Rgb(0xB5452F), Rgb(0xF0997B)),
        ("WarnTextBrush", Rgb(0x854F0B), Rgb(0xFAC775)),
        ("CautionTextBrush", Rgb(0xA65A00), Rgb(0xFAC775)),
        ("WarnPanelBrush", Rgb(0xFAEEDA), Rgb(0x3A2A10)),
        ("WarnLineBrush", Rgb(0xEF9F27), Rgb(0x854F0B)),
        ("PanelBrush", Rgb(0xF1EFE8), Rgb(0x2B2B2B)),
        ("LockBannerBrush", Rgb(0xFBECEA), Rgb(0x3A2220)),
        ("LockBannerLineBrush", Rgb(0xE0B4AA), Rgb(0x6B3A33)),
        ("PlainBannerBrush", Rgb(0xF3F2EC), Rgb(0x2B2B2B)),
        ("PlainBannerLineBrush", Rgb(0xD9D6CA), Rgb(0x4A4944)),
        ("NoticeTextBrush", Rgb(0x5A5A5A), Rgb(0xC8C8C8)),
        ("HighlightLabelTextBrush", Rgb(0x412402), Rgb(0xFAEEDA)),
        ("PickerLitBrush", Rgb(0xB5D4F4), Rgb(0x0C447C)),
        ("ButtonHoverBrush", Color.FromArgb(0x14, 0, 0, 0), Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
        ("ButtonPressedBrush", Color.FromArgb(0x26, 0, 0, 0), Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
    ];

    /// 글자색 · 형광펜 견본 키 — 칠하면 보일 색 그대로 (D-162). 예: <c>TextColor.빨강.Brush</c>
    public static string TextColorKey(string name) => $"TextColor.{name}.Brush";

    public static string HighlightKey(string name) => $"Highlight.{name}.Brush";

    private static IEnumerable<(string Key, Color Color)> Entries(bool dark)
        => Colors.Select(c => (c.Key, dark ? c.Dark : c.Light))
                 .Concat(DocumentColors.Text.Select(p => (TextColorKey(p.Name), DocumentColors.Shown(p, dark))))
                 .Concat(DocumentColors.Highlight.Select(p => (HighlightKey(p.Name), DocumentColors.Shown(p, dark))));

    /// 지금 테마의 앱 색 — 코드에서 그리는 것(그림 손잡이 · 표 고르기 격자)용.
    public static Brush Brush(string key) => Frozen(Entries(IsDark).First(e => e.Key == key).Color);

    /// 한 테마의 색 사전. 창마다 새로 만든다.
    public static ResourceDictionary Palette(bool dark)
    {
        var dictionary = new ResourceDictionary { [PaletteMarker] = true };
        foreach (var (key, color) in Entries(dark)) dictionary[key] = Frozen(color);
        return dictionary;
    }

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
