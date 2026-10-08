using TextBean.Services;

namespace TextBean.Tests;

/// <summary>
/// 말 / 쉼 조각 나누기 (D-180 · D-193 · D-194 회의 모드). 소리는 만들어 넣는다 — 「말」은 0.4초 소리 + 0.1초 끊김의 되풀이
/// (사람 말처럼 낱말 사이가 내려간다). 구간(0.1초 = 1,600 표본) 단위로 길이를 정확히 단언한다.
/// 노트북 실제 녹음으로 본 결과는 add-voice-input/progress.md (LL-099).
/// </summary>
public class SpeechChunkerTests
{
    private const int Window = SpeechChunker.WindowSamples;
    private const float Loud = 0.3f;

    [Fact]
    public void 말_뒤에_쉬면_조각_하나_앞_0점2초_뒤_0점3초()
    {
        var chunks = Run(Silence(1), Syllables(2), Silence(4));

        // 앞 여유 2 + 소리 처음~끝 19 + 뒤 여유 3
        Assert.Equal(new[] { 24 * Window }, chunks.Select(c => c.Length));
        Assert.All(chunks[0][..(2 * Window)], s => Assert.Equal(0f, s));
        Assert.NotEqual(0f, chunks[0][2 * Window + 100]);
    }

    [Fact]
    public void 이십초가_안_되게_말했으면_3초를_쉬어야_자른다()
    {
        // 말 끝 끊김 0.1초 + 2.8초 = 쉼 2.9초 — 이어진다(회의에서 잠깐 생각하는 쉼)
        Assert.Single(Run(Syllables(5), Silence(2.8), Syllables(5), Silence(4)));

        // 0.1초 + 2.9초 = 3.0초 — 경계에서 자른다(발언이 끝났다)
        Assert.Equal(2, Run(Syllables(5), Silence(2.9), Syllables(5), Silence(4)).Count);

        // 10초 말해도 20초가 안 되면 2초 쉼에서는 자르지 않는다(받아쓰기 규칙이었던 5초 기준과 갈리는 자리)
        Assert.Single(Run(Syllables(10), Silence(1.9), Syllables(5), Silence(4)));
    }

    [Fact]
    public void 이십초_넘게_말했으면_0점7초_쉼에서_자른다()
    {
        // 0.1초 + 0.6초 = 0.7초 — 경계에서 자른다
        Assert.Equal(2, Run(Syllables(21), Silence(0.6), Syllables(1), Silence(4)).Count);

        // 0.1초 + 0.5초 = 0.6초 — 이어진다
        Assert.Single(Run(Syllables(21), Silence(0.5), Syllables(1), Silence(4)));
    }

    [Fact]
    public void 쉬지_않고_말하면_28초에서_자른다()
    {
        var chunks = Run(Syllables(35), Silence(4));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(SpeechChunker.MaxChunkWindows * Window, chunks[0].Length);
        Assert.True(SpeechChunker.MaxChunkWindows * Window <= 30 * SpeechChunker.SampleRate);   // Whisper 는 한 번에 30초까지
    }

    [Fact]
    public void 무음_잡음_짧은_딸깍은_버린다()
    {
        Assert.Empty(Run(Silence(10)));
        Assert.Empty(Run(Noise(10, 0.006f)));                     // 16비트 약 110
        Assert.Empty(Run(Noise(10, 0.009f)));                     // 약 170 — 실험의 약한 잡음 176 근처
        Assert.Empty(Run(Noise(10, 0.016f)));                     // 약 300 — 잡음 억제 없는 마이크
        Assert.Empty(Run(Silence(1), Tone(0.7), Silence(4)));     // 말 0.7초 < 0.8초
    }

    [Fact]
    public void 잡음_사이에_가끔_0이_끼어도_잡음은_버린다()
    {
        // 잡음 300 에 2초마다 0.1초씩 정확히 0 — 소음을 최솟값으로 재면 0 이 되어 잡음을 말로 센다
        var noise = Noise(12, 0.016f);
        for (var at = 0; at + Window <= noise.Length; at += 20 * Window) Array.Clear(noise, at, Window);

        Assert.Empty(Run(noise));
    }

    [Fact]
    public void 말이_0점8초면_넘긴다()
        => Assert.Single(Run(Silence(1), Tone(0.8), Silence(4)));

    [Fact]
    public void 노트북_마이크처럼_작은_말도_잡는다()
    {
        // 노트북 마이크 말 구간 중앙값 78~211, 쉴 때는 정확히 0 [실측 — LL-099]. 16비트 약 150 짜리 말
        var chunks = Run(Silence(1), Syllables(3, 0.0065f), Silence(4));

        Assert.Single(chunks);
        Assert.InRange(chunks[0].Length, 29 * Window, 34 * Window);
    }

    [Fact]
    public void 잡음_위의_작은_말도_잡는다()
    {
        // 잡음 170 위에 말 약 450 — 소음의 3배를 문턱으로 하면 놓쳤다 [실측 — 시제품]
        var chunks = Run(Noise(2, 0.009f), Add(Syllables(6, 0.018f), Noise(6, 0.009f, seed: 3)), Noise(4, 0.009f, seed: 4));

        Assert.Single(chunks);
    }

    [Fact]
    public void 말하는_중에_녹음을_시작해도_잡는다()
    {
        var chunks = Run(Syllables(2), Silence(4));

        // 앞 여유 없음 + 19 + 3
        Assert.Equal(new[] { 22 * Window }, chunks.Select(c => c.Length));
    }

    [Fact]
    public void 커진_소음은_조각으로_넘기지_않고_그_위의_말만_넘긴다()
    {
        // 고정 문턱을 넘는 소음 — 처음엔 말로 세어지지만 소음을 배우고 나면 그 조각은 다시 세어 버린다
        var chunks = Run(Silence(1), Noise(30, 0.05f), Add(Syllables(2, 0.5f), Noise(2, 0.05f, seed: 2)), Noise(4, 0.05f, seed: 3));

        Assert.Single(chunks);
        Assert.InRange(chunks[0].Length, 19 * Window, 24 * Window);
    }

    [Fact]
    public void 잘게_나눠_넣어도_결과가_같다()
    {
        var signal = Concat(Silence(1), Syllables(2), Silence(3.5), Syllables(1.2), Silence(4));

        var whole = new List<int>();
        var wholeChunker = new SpeechChunker();
        wholeChunker.ChunkReady += c => whole.Add(c.Length);
        wholeChunker.Push(signal);

        var pieces = new List<int>();
        var pieceChunker = new SpeechChunker();
        pieceChunker.ChunkReady += c => pieces.Add(c.Length);
        for (var at = 0; at < signal.Length; at += 333)
            pieceChunker.Push(signal.AsSpan(at, Math.Min(333, signal.Length - at)));

        Assert.Equal(2, whole.Count);
        Assert.Equal(whole, pieces);
    }

    [Fact]
    public void 멈추면_말하던_조각을_남은_표본까지_내보내고_비운다()
    {
        var chunker = new SpeechChunker();
        var chunks = new List<float[]>();
        chunker.ChunkReady += chunks.Add;

        chunker.Push(Concat(Silence(1), Syllables(1)));
        chunker.Push(Tone(0.05));                                  // 구간을 채우지 못한 800 표본
        chunker.Flush();

        Assert.Single(chunks);
        Assert.Equal(2 * Window + 10 * Window + Window / 2, chunks[0].Length);

        // 비운 뒤에는 남은 것이 없다
        chunker.Flush();
        Assert.Single(chunks);
    }

    [Fact]
    public void 쉬는_중에_멈추면_더_내보내지_않는다()
    {
        var chunker = new SpeechChunker();
        var count = 0;
        chunker.ChunkReady += _ => count++;

        chunker.Push(Concat(Silence(1), Syllables(1), Silence(4)));
        chunker.Flush();

        Assert.Equal(1, count);
    }

    [Fact]
    public void Reset_뒤에는_담던_말을_버린다()
    {
        var chunker = new SpeechChunker();
        var count = 0;
        chunker.ChunkReady += _ => count++;

        chunker.Push(Concat(Silence(1), Syllables(1)));
        chunker.Reset();
        chunker.Push(Silence(1));
        chunker.Flush();

        Assert.Equal(0, count);
    }

    private static List<float[]> Run(params float[][] parts)
    {
        var chunker = new SpeechChunker();
        var chunks = new List<float[]>();
        chunker.ChunkReady += chunks.Add;
        chunker.Push(Concat(parts));
        return chunks;
    }

    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    private static float[] Silence(double seconds) => new float[Samples(seconds)];

    private static float[] Tone(double seconds, float amplitude = Loud)
    {
        var samples = new float[Samples(seconds)];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = amplitude * (float)Math.Sin(2 * Math.PI * 220 * i / SpeechChunker.SampleRate);
        return samples;
    }

    /// 0.4초 소리 + 0.1초 끊김의 되풀이.
    private static float[] Syllables(double seconds, float amplitude = Loud)
    {
        var samples = new float[Samples(seconds)];
        var tone = Tone(0.4, amplitude);
        for (var at = 0; at < samples.Length; at += Samples(0.5))
            tone.AsSpan(0, Math.Min(tone.Length, samples.Length - at)).CopyTo(samples.AsSpan(at));
        return samples;
    }

    private static float[] Noise(double seconds, float amplitude, int seed = 1)
    {
        var random = new Random(seed);
        var samples = new float[Samples(seconds)];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(random.NextDouble() * 2 - 1) * amplitude;
        return samples;
    }

    private static float[] Add(float[] a, float[] b)
    {
        var sum = new float[a.Length];
        for (var i = 0; i < a.Length; i++) sum[i] = a[i] + b[i];
        return sum;
    }

    private static int Samples(double seconds) => (int)Math.Round(seconds * SpeechChunker.SampleRate);
}
