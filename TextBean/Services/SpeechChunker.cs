namespace TextBean.Services;

/// <summary>
/// 16kHz 모노 소리를 「말 → 쉼」마다 조각으로 자른다 (D-180). 말이 거의 없는 조각은 버린다 — Whisper 는 무음 · 잡음에서
/// 「감사합니다」 같은 없는 문장을 지어내고, 「말 없음 확률」은 늘 0 이라 거르는 데 못 쓴다 [실측 — add-voice-input research 0-1절] (D-182).
/// 말 / 쉼은 0.1초 구간의 소리 크기(RMS)로 가른다. 문턱은 고정 값과 「주변 소음 × 3」 중 큰 쪽이다. 주변 소음은 말이 아닌 구간에서만
/// 잰다 — 말하는 중에 녹음을 시작해도 말소리를 소음으로 삼지 않게. 소음이 커서 말로 세어지면 12초마다 끊기는데, 그때 그 조각에서
/// 가장 조용했던 구간을 소음으로 넣어 문턱을 올린다(소음만 계속 받아쓰지 않게).
/// 녹음 스레드 하나에서만 부른다(스레드 안전하지 않다).
/// </summary>
public sealed class SpeechChunker
{
    public const int SampleRate = 16000;

    // 아래 값은 합성 음성 실험에서 잡은 처음 값이다 — 실제 마이크로 맞출 자리는 여기 한 곳이다 (plan 「미확정」).
    public const int WindowSamples = SampleRate / 10;
    public const float MinSpeechLevel = 600f / 32768f;   // 16비트 600 — 합성 음성 말 6,000 대 · 약한 잡음 176 [실측]
    public const float NoiseFactor = 3f;
    public const int FloorWindows = 100;                 // 소음은 최근 10초 몫에서 가장 조용한 값
    public const int PauseWindows = 7;                   // 0.7초 쉬면 자른다
    public const int MaxChunkWindows = 120;              // 12초면 말 중간이라도 자른다
    public const int MinSpeechWindows = 3;               // 말 부분이 0.3초 미만이면 버린다
    public const int PreRollWindows = 2;                 // 말 시작 앞 0.2초 — 첫 소리가 잘리지 않게
    public const int TailWindows = 3;                    // 말 끝 뒤 0.3초

    private readonly float[] _partial = new float[WindowSamples];
    private int _partialCount;

    private readonly Queue<float[]> _preRoll = new();
    private readonly Queue<float> _noiseLevels = new();
    private readonly List<float[]> _chunk = [];
    private bool _inChunk;
    private int _speechWindows;
    private int _silenceRun;
    private float _quietestInChunk;
    private int _quietWindowsInChunk;

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
            OnWindow((float[])_partial.Clone());
        }
    }

    /// 멈출 때 부른다. 말하던 중이면 남은 것까지 조각으로 내보낸다.
    public void Flush()
    {
        if (_inChunk && _partialCount > 0) _chunk.Add(_partial[.._partialCount]);
        _partialCount = 0;

        if (_inChunk) EndChunk();
        Reset();
    }

    /// 담고 있던 소리를 모두 지운다(취소 · 멈춤 뒤) — 소리는 메모리에만 두고 남기지 않는다 (D-188).
    public void Reset()
    {
        foreach (var window in _chunk) Array.Clear(window);
        foreach (var window in _preRoll) Array.Clear(window);
        Array.Clear(_partial);

        _chunk.Clear();
        _preRoll.Clear();
        _noiseLevels.Clear();
        _partialCount = 0;
        _inChunk = false;
        _speechWindows = 0;
        _silenceRun = 0;
    }

    private void OnWindow(float[] window)
    {
        var level = Rms(window);
        var speech = level >= Threshold();

        if (!_inChunk)
        {
            if (!speech)
            {
                AddNoise(level);
                _preRoll.Enqueue(window);
                if (_preRoll.Count > PreRollWindows) Array.Clear(_preRoll.Dequeue());
                return;
            }

            _inChunk = true;
            _chunk.AddRange(_preRoll);
            _preRoll.Clear();
            _speechWindows = 0;
            _silenceRun = 0;
            _quietestInChunk = float.MaxValue;
            _quietWindowsInChunk = 0;
        }

        _chunk.Add(window);
        _quietestInChunk = Math.Min(_quietestInChunk, level);
        if (speech)
        {
            _speechWindows++;
            _silenceRun = 0;
        }
        else
        {
            AddNoise(level);
            _silenceRun++;
            _quietWindowsInChunk++;
        }

        if (_silenceRun >= PauseWindows)
        {
            EndChunk();
            return;
        }

        // 쉬지 않고 길게 말하면 끊어서라도 넘긴다 — 글자가 너무 늦게 들어가지 않게. 말은 이어지는 중이라 새 조각을 바로 연다
        if (_chunk.Count >= MaxChunkWindows)
        {
            // 12초 내내 한 번도 쉬지 않았다면 말이 아니라 커진 주변 소음으로 본다 — 사람 말은 낱말 사이에 문턱 아래로 내려간다 [추정 — 실기에서 확인].
            // 예전 조용한 값이 남아 있으면 문턱이 오르지 않으니 소음 기준을 이 조각의 가장 조용한 값으로 바꾼다
            if (_quietWindowsInChunk == 0)
            {
                _noiseLevels.Clear();
                AddNoise(_quietestInChunk);
            }

            Emit(_chunk, _speechWindows);
            _chunk.Clear();
            _speechWindows = 0;
            _silenceRun = 0;
            _quietestInChunk = float.MaxValue;
            _quietWindowsInChunk = 0;
        }
    }

    private void EndChunk()
    {
        // 말 끝 뒤의 쉼은 0.3초만 남긴다. 긴 무음이 붙으면 그 자리에 없는 문장을 지어낸다 [실측]
        var trailing = Math.Max(0, _silenceRun - TailWindows);
        var keep = _chunk.Count - trailing;
        for (var i = keep; i < _chunk.Count; i++) Array.Clear(_chunk[i]);

        Emit(_chunk.GetRange(0, keep), _speechWindows);

        _chunk.Clear();
        _inChunk = false;
        _speechWindows = 0;
        _silenceRun = 0;
    }

    private void Emit(List<float[]> windows, int speechWindows)
    {
        var length = windows.Sum(w => w.Length);
        if (speechWindows < MinSpeechWindows || length == 0)
        {
            foreach (var window in windows) Array.Clear(window);
            return;
        }

        var samples = new float[length];
        var at = 0;
        foreach (var window in windows)
        {
            window.CopyTo(samples, at);
            at += window.Length;
            Array.Clear(window);
        }

        ChunkReady?.Invoke(samples);
    }

    private float Threshold()
        => _noiseLevels.Count == 0 ? MinSpeechLevel : Math.Max(MinSpeechLevel, _noiseLevels.Min() * NoiseFactor);

    private void AddNoise(float level)
    {
        _noiseLevels.Enqueue(level);
        if (_noiseLevels.Count > FloorWindows) _noiseLevels.Dequeue();
    }

    private static float Rms(float[] window)
    {
        double sum = 0;
        foreach (var sample in window) sum += (double)sample * sample;
        return (float)Math.Sqrt(sum / window.Length);
    }
}
