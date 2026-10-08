using System.ComponentModel;
using System.Windows.Media;
using TextBean.ViewModels;

namespace TextBean.Views.Platform;

/// <summary>
/// 화면 글꼴 (D-148 · D-155). 한글 · 영문을 한 글꼴로 그린다 — 영문 고정폭 글꼴에 대체 한글이 섞이면
/// 형광펜 띠가 글꼴마다 그 높이로 칠해져 배율 125% 등에서 1~2px 어긋난다 [실측].
/// <list type="bullet">
/// <item><b>Fixed</b> — 앱 안 D2Coding. 찾기 칸 · 상세 검색 칸 · 키 칸은 늘 이것(고정폭이라 l · I · 1, O · 0 을 가른다, D-156).</item>
/// <item><b>Body</b> — 사용자가 고른 본문 글꼴(.tbx · .txt). 바뀜 알림이 있어 열린 탭이 바로 따라온다.
/// XAML 은 <c>{Binding Path=(platform:AppFonts.Body)}</c> 로 읽는다 — <c>{x:Static}</c> 은 한 번만 읽는다.</item>
/// </list>
/// D2Coding 주소에 어셈블리 이름을 적는다 — "pack://application" 만 쓰면 시험 프로세스(다른 실행 어셈블리)에서 못 찾는다.
/// </summary>
public static class AppFonts
{
    public static FontFamily Fixed { get; } = new(new Uri("pack://application:,,,/TextBean;component/Assets/Fonts/"), "./#D2Coding");

    private static FontFamily _body = Fixed;

    public static FontFamily Body
    {
        get => _body;
        private set
        {
            if (ReferenceEquals(_body, value)) return;
            _body = value;
            StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Body)));
        }
    }

    /// WPF 정적 속성 바인딩이 듣는 이름이다 — 이름을 바꾸면 바인딩이 조용히 처음 값에 머문다.
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    /// 후보의 실제 글꼴. 앱 안 글꼴이면 Fixed, 아니면 Windows 에 설치된 글꼴 이름(없는 PC 에선 WPF 가 기본 글꼴로 그린다).
    public static FontFamily For(FontChoice choice) => choice.FamilyName is null ? Fixed : new FontFamily(choice.FamilyName);

    public static void Select(FontChoice choice)
    {
        if (ReferenceEquals(Body, Fixed) && choice.FamilyName is null) return;
        if (Body.Source == choice.FamilyName) return;

        Body = For(choice);
    }
}
