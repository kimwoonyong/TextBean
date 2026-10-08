namespace TextBean.Services.Interfaces;

public enum DictationState
{
    Idle,

    /// 마이크로 듣는 중. 생긴 조각은 차례로 받아써 넣는다.
    Listening,

    /// 듣기는 멈췄고 남은 조각을 받아써 넣는 중.
    Finishing
}

/// <summary>
/// 음성 입력 — 한 번 켜면 계속 듣고, 쉼마다 조각을 받아써 시작한 탭의 시작한 자리에 이어 넣는다 (D-180 · D-184).
/// 탭은 <c>object</c> 로 받는다 — Services 가 ViewModels 를 알면 안 된다(PROHIBITED-ARCH-02). 구현은 Views/Platform 에 있다.
/// 상태 변화는 UI 스레드에서 알린다.
/// </summary>
public interface IDictation : IDisposable
{
    DictationState State { get; }

    /// 모델을 확인 · 여는 중(듣기는 이미 시작했다 — 그 사이 생긴 조각은 기다린다).
    bool IsPreparing { get; }

    /// 받아쓰기를 기다리거나 받아쓰는 중인 조각 수.
    int Pending { get; }

    TimeSpan Elapsed { get; }

    /// 받아쓴 글자가 들어갈 탭. 쉬면 null.
    object? Target { get; }

    event EventHandler? StateChanged;

    /// 이 탭에서 시작할 수 있나 — 문서가 있고 읽기 전용이 아니고 본문이 있다.
    bool CanStart(object? tab);

    /// 시작한다. 모델 파일이 없으면 고르게 하고, 마이크 · 모델을 못 쓰면 오류 창을 띄우고 쉼으로 남는다.
    Task StartAsync(object tab);

    /// 듣기를 멈추고 남은 조각까지 넣는다 (트레이 숨김 — D-185).
    void Stop();

    /// 지금 멈추고 남은 조각은 버린다 (잠그기 · 절전 · 금고 바꾸기 · 종료 · 대상 탭 닫힘 — D-186).
    void Cancel();
}
