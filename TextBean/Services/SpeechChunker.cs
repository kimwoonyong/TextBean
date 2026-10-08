namespace TextBean.Services;

/// <summary>
/// 16kHz 모노 소리를 「말 → 쉼」마다 조각으로 자른다 (D-180 · D-193). 말 / 쉼은 0.1초 구간의 소리 크기(RMS)로 가른다.
///
/// 문턱 = max(고정 80, 주변 소음 × 2). 주변 소음은 최근 10초 구간 크기의 하위 10% — 말소리에도 낱말 사이 조용한 틈이 있어
/// 말하는 중에도 소음을 잴 수 있다(말하면서 녹음을 시작해도 첫 틈에서 바로잡힌다). 소리 크기는 마이크마다 수십 배 다르다 —
/// 합성 음성(6,000대)에 맞춘 고정 600 은 노트북 마이크(말 100~600)의 말을 90% 넘게 버렸다 [실측 — LL-099].
///
/// 조각은 짧으면 안 된다 — Whisper 는 1~2초 조각에서 「이 시각 세계였습니다」 같은 문장을 지어내고 되풀이하느라 오래 걸리고,
/// 길수록 정확했다(노트북 녹음 25초 통째가 가장 정확, 말하는 속도의 약 0.6배 시간) [실측]. 「말하기」는 회의 기록용이다 (D-194) —
/// 말한 길이가 20초 이상이면 0.7초 쉼에서, 그보다 짧으면 3초 쉬어야(발언이 끝나야) 자른다. 쉬지 않으면 28초마다 자른다.
/// 내보낼 때 그때의 문턱으로 말 구간을 다시 센다 — 소음을 배우기 전에 말로 셌던 잡음 조각을 버린다.
/// 녹음 스레드 하나에서만 부른다(스레드 안전하지 않다). 소리는 메모리에만 두고 쓴 뒤 지운다 (D-188).
/// </summary>
public sealed class SpeechChunker
{
    public const int SampleRate = 16000;

    // 노트북 마이크 실제 대화 녹음 5개와 합성 경우로 맞춘 값이다 (D-193 · D-194) — 다시 맞출 자리는 여기 한 곳이다.
    public const int WindowSamples = SampleRate / 10;
    public const float MinSpeechLevel = 80f / 32768f;      // 노트북 마이크 말 구간 중앙값 78~211, 쉴 때는 정확히 0 [실측]
    public const float NoiseFactor = 2f;                   // 3 이면 잡음(170) 위 작은 말(약 450)을 놓쳤다 [실측 — 시제품]
    public const int FloorWindows = 100;                   // 소음은 최근 10초 몫에서 잰다
    public const int PauseWindows = 7;                     // 20초 이상 말했으면 0.7초 쉼에서 자른다
    public const int LongPauseWindows = 30;                // 그보다 짧으면 3초 쉬어야(발언이 끝나야) 자른다
    public const int MinChunkWindows = 200;                // 「20초 이상 말했다」의 20초
    public const int MaxChunkWindows = 280;                // 28초면 말 중간이라도 자른다 — Whisper 는 한 번에 30초까지 듣는다
    public const int MinSpeechWindows = 8;                 // 말 부분이 0.8초 미만이면 버린다
    public const int PreRollWindows = 2;                   // 말 시작 앞 0.2초 — 첫 소리가 잘리지 않게
    public const int TailWindows = 3;                      // 말 끝 뒤 0.3초

    private readonly float[] _partial = new float[WindowSamples];
    private int _partialCount;

    private readonly Queue<Window> _preRoll = new();
    private readonly Queue<float> _recentLevels = new();
    private readonly List<Window> _chunk = [];
    private bool _inChunk;
    private int _silenceRun;

    /// 조각 하나(16kHz 모노). 받은 쪽이 다 쓰고 지운다 — 이 클래스는 넘긴 배열을 다시 쓰지 않는다.
    public event Action<float[]>? ChunkReady;

    public void Push(ReadOnlySpan<float> samples)
    {
        while (!samples.IsEmpty)
        {
            var take = Math.Min(WindowSamples - _partialCount, samples.Length);
            samples[..take].CopyTo(_partial.AsSpan(_partialCount));
            _partialCount += take;
            samples = samples[take..];

            if (_partialCount < WindowSamples) return;

            _partialCount = 0;
            var window = (float[])_partial.Clone();
            OnWindow(new Window(window, Rms(window)));
        }
    }

    /// 멈출 때 부른다. 말하던 중이면 남은 것까지 조각으로 내보낸다.
    public void Flush()
    {
        if (_inChunk && _partialCount > 0)
        {
            var rest = _partial[.._partialCount];
            _chunk.Add(new Window(rest, Rms(rest)));
        }
        _partialCount = 0;

        if (_inChunk) EndChunk();
        Reset();
    }

    /// 담고 있던 소리를 모두 지운다(취소 · 멈춤 뒤) — 소리는 메모리에만 두고 남기지 않는다 (D-188).
    public void Reset()
    {
        foreach (var window in _chunk) Array.Clear(window.Samples);
        foreach (var window in _preRoll) Array.Clear(window.Samples);
        Array.Clear(_partial);

        _chunk.Clear();
        _preRoll.Clear();
        _recentLevels.Clear();
        _partialCount = 0;
        _inChunk = false;
        _silenceRun = 0;
    }

    private void OnWindow(Window window)
    {
        var speech = window.Level >= Threshold();
        _recentLevels.Enqueue(window.Level);
        if (_recentLevels.Count > FloorWindows) _recentLevels.Dequeue();

        if (!_inChunk)
        {
            if (!speech)
            {
                _preRoll.Enqueue(window);
                if (_preRoll.Count > PreRollWindows) Array.Clear(_preRoll.Dequeue().Samples);
                return;
            }

            _inChunk = true;
            _chunk.AddRange(_preRoll);
            _preRoll.Clear();
            _silenceRun = 0;
        }

        _chunk.Add(window);
        _silenceRun = speech ? 0 : _silenceRun + 1;

        var spoken = _chunk.Count - _silenceRun;
        if (_silenceRun >= (spoken >= MinChunkWindows ? PauseWindows : LongPauseWindows))
        {
            EndChunk();
            return;
        }

        // 쉬지 않고 길게 말하면 끊어서라도 넘긴다 — 글자가 너무 늦게 들어가지 않게. 말은 이어지는 중이라 새 조각을 바로 연다
        if (_chunk.Count >= MaxChunkWindows)
        {
            Emit(_chunk);
            _chunk.Clear();
            _silenceRun = 0;
        }
    }

    private void EndChunk()
    {
        // 말 끝 뒤의 쉼은 0.3초만 남긴다. 긴 무음이 붙으면 그 자리에 없는 문장을 지어낸다 [실측]
        var trailing = Math.Max(0, _silenceRun - TailWindows);
        var keep = _chunk.Count - trailing;
        for (var i = keep; i < _chunk.Count; i++) Array.Clear(_chunk[i].Samples);

        Emit(_chunk.GetRange(0, keep));

        _chunk.Clear();
        _inChunk = false;
        _silenceRun = 0;
    }

    private void Emit(List<Window> windows)
    {
        // 지금 문턱으로 다시 센다 — 소음을 배우기 전에 말로 센 잡음은 여기서 걸러진다
        var threshold = Threshold();
        if (windows.Count(w => w.Level >= threshold) < MinSpeechWindows)
        {
            foreach (var window in windows) Array.Clear(window.Samples);
            return;
        }

        var samples = new float[windows.Sum(w => w.Samples.Length)];
        var at = 0;
        foreach (var window in windows)
        {
            window.Samples.CopyTo(samples, at);
            at += window.Samples.Length;
            Array.Clear(window.Samples);
        }

        ChunkReady?.Invoke(samples);
    }

    private float Threshold()
    {
        if (_recentLevels.Count == 0) return MinSpeechLevel;

        // 최솟값이 아니라 하위 10% — 잡음 사이에 가끔 정확히 0 이 끼는 마이크(잡음 차단이 깜빡임)에서 최솟값은 0 이 되어 잡음을 말로 센다
        var sorted = _recentLevels.Order().ToArray();
        return Math.Max(MinSpeechLevel, sorted[sorted.Length / 10] * NoiseFactor);
    }

    private static float Rms(float[] window)
    {
        double sum = 0;
        foreach (var sample in window) sum += (double)sample * sample;
        return (float)Math.Sqrt(sum / window.Length);
    }

    private readonly record struct Window(float[] Samples, float Level);
}
