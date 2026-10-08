namespace TextBean.ViewModels;

/// 테마 후보 하나 (D-161). 셸은 WPF 를 모른다 — 테마는 id 로만 다루고, 화면(AppTheme)이 실제 색으로 바꾼다.
public sealed record ThemeChoice(string Id, string Label);

/// 보기 ▸ 테마 목록 한 줄. 지금 테마면 ✓.
public sealed record ThemeMenuItem(string Id, string Label, bool IsCurrent);

/// 테마 후보 목록 — 밝게 · 어둡게 두 개(사용자 판정 Q-1).
public static class ThemeChoices
{
    public const string DefaultId = "light";
    public const string DarkId = "dark";

    public static IReadOnlyList<ThemeChoice> All { get; } =
    [
        new(DefaultId, "밝게"),
        new(DarkId, "어둡게"),
    ];

    /// 모르는 id(손으로 고친 설정 등)는 밝게.
    public static ThemeChoice Find(string? id) => All.FirstOrDefault(c => c.Id == id) ?? All[0];
}
