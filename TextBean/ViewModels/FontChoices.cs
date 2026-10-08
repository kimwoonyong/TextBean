namespace TextBean.ViewModels;

/// <summary>
/// 본문 글꼴 후보 하나 (D-155). FamilyName 이 null 이면 앱 안에 넣은 D2Coding, 아니면 Windows 에 설치된 글꼴 이름.
/// 편집기 · 셸은 WPF 를 모른다 — 글꼴은 이름으로만 다루고, 화면(AppFonts)이 실제 글꼴로 바꾼다.
/// </summary>
public sealed record FontChoice(string Id, string Label, string? FamilyName);

/// 도구 모음 「글꼴 ▾」 목록 한 줄. 지금 글꼴이면 ✓.
public sealed record FontMenuItem(string Id, string Label, bool IsCurrent);

/// <summary>
/// 본문 글꼴 후보 목록 — 한 곳에 둔다 (D-157).
/// <para>
/// 나중에 늘리기: Windows 설치 글꼴이면 이 목록에 한 줄. 앱에 넣는 글꼴이면 자원(csproj) 등록과 AppFonts 도 함께.
/// 후보는 한글 · 영문을 한 글꼴로 그려야 한다 — 섞이면 형광펜 띠가 들쭉하다 [실측 — D-148]. 시험이 목록 전체를 돈다.
/// </para>
/// </summary>
public static class FontChoices
{
    public const string DefaultId = "d2coding";

    public static IReadOnlyList<FontChoice> All { get; } =
    [
        new(DefaultId, "D2Coding", null),
        new("malgun", "맑은 고딕", "Malgun Gothic"),
    ];

    /// 모르는 id(지운 후보 · 손으로 고친 설정)는 기본 글꼴.
    public static FontChoice Find(string? id) => All.FirstOrDefault(c => c.Id == id) ?? All[0];
}
