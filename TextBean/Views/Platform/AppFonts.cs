using System.Windows.Media;

namespace TextBean.Views.Platform;

/// <summary>
/// 본문 글꼴 (D-148). 한글 · 영문을 한 글꼴(D2Coding)로 그린다 — 영문 고정폭 글꼴에 대체 한글이 섞이면
/// 형광펜 띠가 글꼴마다 그 높이로 칠해져 배율 125% 등에서 1~2px 어긋난다 [실측]. 고정폭이라 l · I · 1, O · 0 을 가른다.
/// 주소에 어셈블리 이름을 적는다 — "pack://application" 만 쓰면 시험 프로세스(다른 실행 어셈블리)에서 못 찾는다.
/// </summary>
public static class AppFonts
{
    public static FontFamily Body { get; } = new(new Uri("pack://application:,,,/TextBean;component/Assets/Fonts/"), "./#D2Coding");
}
