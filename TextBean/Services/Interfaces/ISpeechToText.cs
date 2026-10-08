namespace TextBean.Services.Interfaces;

/// <summary>
/// 소리 조각(16kHz 모노)을 글자로 받아쓴다 — PC 안에서만 (D-179). 받아쓴 글자는 문서 내용이다 — 로그 · 오류 문구에 넣지 않는다 (D-188).
/// </summary>
public interface ISpeechToText : IDisposable
{
    /// <summary>
    /// 이 경로의 모델 파일을 쓸 수 없으면 사용자에게 보일 이유, 쓸 수 있어 보이면 null. 있는지 · 크기 · 금고 밖인지만 본다 —
    /// 내용(해시) 확인은 시간이 걸려 <see cref="PrepareAsync"/> 가 한다.
    /// </summary>
    string? ModelProblem(string? modelPath, string? vaultRoot);

    /// 모델 · DLL 을 확인하고 연다. 한 번 확인한 파일은 바뀌지 않았으면 다시 확인하지 않는다. 못 쓰면 <see cref="VoiceUnavailableException"/>.
    Task PrepareAsync(string modelPath, CancellationToken ct);

    /// <see cref="PrepareAsync"/> 뒤에 부른다. 조각 하나를 받아쓴다.
    Task<string> TranscribeAsync(float[] samples, CancellationToken ct);
}

/// <summary>
/// 음성 입력을 쓸 수 없다(마이크 없음 · 권한 막힘 · 모델 다름 · DLL 다름 · 런타임 없음). 메시지는 대화상자에 그대로 보인다 —
/// 받아쓴 글자는 넣지 않는다 (PROHIBITED-CUSTOM-04).
/// </summary>
public sealed class VoiceUnavailableException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
