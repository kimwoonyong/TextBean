namespace TextBean.Models;

public sealed class AppSettings
{
    public string? RootPath { get; set; }

    public double WindowWidth { get; set; } = 1000;

    public double WindowHeight { get; set; } = 640;

    /// 처음 ✕ 로 트레이에 숨길 때의 안내를 봤는가 (D-096). 키 · 잠금 상태는 여기 두지 않는다 (D-053).
    public bool TrayNoticeShown { get; set; }

    /// 단축키 (D-110). 동작 id → 키 글자("Ctrl+S"), 빈 글자 = 없음. 없는 id 는 기본 키다.
    public Dictionary<string, string>? Shortcuts { get; set; }

    /// 본문 글꼴 후보 id (D-155). 없거나 모르는 값이면 기본(D2Coding).
    public string? BodyFont { get; set; }

    /// 테마 후보 id (D-161). 없거나 모르는 값이면 밝게.
    public string? Theme { get; set; }

    /// 음성 인식 모델 파일 경로 (add-voice-input · ESC-07). 비밀이 아니다. 없거나 그 파일이 없으면 음성 입력을 켤 때 고른다.
    public string? VoiceModelPath { get; set; }
}
