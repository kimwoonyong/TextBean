using TextBean.Models;

namespace TextBean.Services.Interfaces;

public interface IDialogService
{
    string? PickFolder(string suggestedPath, string title);

    string? PromptText(string title, string initial);

    /// <summary>
    /// 가림 처리한 키 입력창. 평문 입력창(PromptText)을 쓰면 키가 화면에 보이고 실행취소 기록에 남는다.
    /// <paramref name="confirm"/> 이면 두 번 입력받는다. 취소하면 null.
    /// </summary>
    KeyEntry? PromptKey(string title, string message, bool confirm);

    /// <summary>
    /// 문서 파일 하나를 고른다. 금고 안인지는 호출자가 검사한다 —
    /// 이 대화상자는 파일 시스템 전체를 볼 수 있다.
    /// </summary>
    string? PickDocument(string suggestedFolder, string title);

    /// 그림 파일 하나를 고른다(그림 넣기 — D-140). 금고 밖일 수 있다 — 읽기는 ImageFileImport 한 곳만 한다.
    string? PickImageFile();

    /// 음성 인식 모델 파일 하나를 고른다(D-179). 금고 밖에 있다 — 확인 · 읽기는 WhisperSpeechToText 한 곳만 한다.
    string? PickModelFile();

    bool Confirm(string title, string message);

    void Error(string title, string message);

    /// 첫 ✕ 안내(트레이로 숨기기). 숨기기면 true, 취소면 false (D-096 · D-109).
    bool ConfirmHideToTray();

    /// <summary>
    /// 이 서비스의 대화상자(MessageBox · 공용 대화상자 · 입력창)가 떠 있는가.
    /// MessageBox 는 WPF 스레드 모달(ComponentDispatcher.IsThreadModal)에 걸리지 않는다 [실측] —
    /// 트레이 메뉴처럼 모달이 끄지 못하는 곳에서 "대화상자 밑에서 닫기"를 막으려면 이것을 본다 (D-104).
    /// </summary>
    bool IsShowing { get; }

    /// <summary>
    /// 돌려받은 것을 해제할 때까지 대화상자를 띄우지 않고 취소로 돌려준다 — 확인은 false, 입력 · 고르기는 null, 오류는 로그만.
    /// 겹쳐 불러도 된다. Windows 종료 · 절전 대기 중 다른 작업이 띄운 창에 대기가 묶이지 않게 한다 (D-101 · D-104).
    /// </summary>
    IDisposable Suppress();
}
