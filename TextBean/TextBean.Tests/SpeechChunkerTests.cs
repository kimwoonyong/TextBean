using TextBean.Services;

namespace TextBean.Tests;

/// <summary>
/// 말 / 쉼 조각 나누기 (D-180 · D-182). 소리는 만들어 넣는다 — 「말」은 0.4초 소리 + 0.1초 끊김의 되풀이(사람 말처럼 낱말 사이가 내려간다).
/// 구간(0.1초 = 1,600 표본) 단위로 길이를 정확히 단언한다.
/// </summary>
public class SpeechChunkerTests
{
    private const int Window = SpeechChunker.WindowSamples;
    private const float Loud = 0.3f;

    [Fact]
    public void 말_뒤에_쉬면_조각_하나_앞_0점2초_뒤_0점3초()
    {
        var chunks = Run(Silence(1), Syllables(2), Silence(1));

        // 앞 여유 2 + 소리 처음~끝 19 + 뒤 여유 3
        Assert.Equal(new[] { 24 * Window }, chunks.Select(c => c.Length));
        Assert.All(chunks[0][..(2 * Window)], s => Assert.Equal(0f, s));
        Assert.NotEqual(0f, chunks[0][2 * Window + 100]);
    }

    [Fact]
    public void 쉼이_0점7초에_못_미치면_한_조각으로_이어진다()
        // 말 끝 끊김 0.1초 + 0.5초 = 쉼 0.6초
        => Assert.Single(Run(Syllables(1), Silence(0.5), Syllables(1), Silence(1)));

    [Fact]
    public void 쉼이_0점7초면_자른다()
    {
        // 말 끝 끊김 0.1초 + 0.6초 = 쉼 0.7초 — 경계
        Assert.Equal(2, Run(Syllables(1), Silence(0.6), Syllables(1), Silence(1)).Count);
        Assert.Equal(2, Run(Syllables(1), Silence(0.8), Syllables(1), Silence(1)).Count);
    }

    [Fact]
    public void 쉬지_않고_말하면_12초에서_자른다()
    {
        var chunks = Run(Syllables(15), Silence(1));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(SpeechChunker.MaxChunkWindows * Window, chunks[0].Length);
    }

    [Fact]
    public void 무음_약한_잡음_짧은_딸깍은_버린다()
    {
        Assert.Empty(Run(Silence(10)));
        Assert.Empty(Run(Noise(10, 0.006f)));                     // 16비트 약 110 — 실험의 약한 잡음 176 보다 작다
        Assert.Empty(Run(Silence(1), Tone(0.2), Silence(1)));     // 말 0.2초 < 0.3초
    }

    [Fact]
    public void 말이_0점3초면_넘긴다()
        => Assert.Single(Run(Silence(1), Tone(0.3), Silence(1)));

    [Fact]
    public void 말하는_중에_녹음을_시작해도_잡는다()
    {
        var chunks = Run(Syllables(2), Silence(1));

        // 앞 여유 없음 + 19 + 3
        Assert.Equal(new[] { 22 * Window }, chunks.Select(c => c.Length));
    }

    [Fact]
    public void 커진_소음은_한_번만_넘기고_그다음부터는_말로_세지_않는다()
    {
        // 고정 문턱(16비트 600)을 넘는 소음 — 처음 12초는 말로 세어진다. 그 뒤로는 문턱이 올라 소음 조각이 더 나오지 않는다
        var chunks = Run(Silence(1), Noise(30, 0.05f), Add(Syllables(2, 0.5f), Noise(2, 0.05f, seed: 2)), Noise(1, 0.05f, seed: 3));

        Assert.Equal(2, chunks.Count);
        Assert.Equal(SpeechChunker.MaxChunkWindows * Window, chunks[0].Length);
        Assert.InRange(chunks[1].Length, 19 * Window, 24 * Window);
    }

    [Fact]
    public void 잘게_나눠_넣어도_결과가_같다()
    {
        var signal = Concat(Silence(1), Syllables(2), Silence(0.8), Syllables(1.2), Silence(1));

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

        chunker.Push(Concat(Silence(1), Syllables(1), Silence(1)));
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
