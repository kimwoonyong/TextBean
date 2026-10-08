namespace TextBean.Services.Interfaces;

/// <summary>
/// 기본 마이크에서 듣는다. 소리는 메모리에만 — 디스크에 쓰지 않는다 (D-188).
/// </summary>
public interface IVoiceRecorder : IDisposable
{
    /// 16kHz 모노 소리. 녹음 스레드에서 올라온다.
    event Action<float[]>? Samples;

    /// 녹음이 스스로 끝났다(장치 빠짐 등) — 이유는 <see cref="VoiceUnavailableException"/>. <see cref="Stop"/> 으로 끝낸 것은 올리지 않는다.
    event Action<VoiceUnavailableException>? Failed;

    /// 듣기 시작한다. 마이크가 없거나 막혀 있으면 <see cref="VoiceUnavailableException"/>.
    void Start();

    void Stop();
}
