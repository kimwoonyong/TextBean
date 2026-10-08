using System.Security.Cryptography;
using TextBean.Services;
using TextBean.Services.Interfaces;

namespace TextBean.Tests;

/// <summary>
/// 받아쓰기 엔진의 지킴 줄 (D-183 · D-188): exe 옆 DLL 4개의 고정 해시 · 폴더에 다른 파일 없음 · 모델 파일 크기 · 내용 · 금고 밖.
/// 실제 모델 시험은 환경 변수가 있을 때만 돈다 — 574MB 모델을 시험 저장소에 두지 않는다.
/// </summary>
public class VoiceNativeTests
{
    private static string BuildNatives => Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64");

    [Fact]
    public void 고정_해시가_빌드_출력의_DLL_과_같고_그_4개뿐이다()
    {
        var files = Directory.GetFileSystemEntries(BuildNatives).Select(Path.GetFileName).Order().ToArray();

        Assert.Equal(WhisperSpeechToText.NativeSha256.Keys.Order(), files);
        foreach (var (name, expected) in WhisperSpeechToText.NativeSha256)
            Assert.Equal(expected, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(BuildNatives, name)))));
    }

    [Fact]
    public async Task 바뀐_DLL_은_열지_않는다()
    {
        using var app = CopyOfNatives();
        var dll = Path.Combine(app.Root, "runtimes", "win-x64", "ggml-cpu-whisper.dll");
        File.AppendAllText(dll, "x");

        var error = await PrepareFails(app.Root);
        Assert.Contains("게시한 것과 달라", error.Message);
    }

    [Fact]
    public async Task DLL_폴더에_다른_파일이_있으면_열지_않는다()
    {
        // 의존 DLL 은 그 DLL 의 폴더에서 먼저 찾는다 — 가짜 런타임 DLL 을 옆에 두는 길을 막는다
        using var app = CopyOfNatives();
        File.WriteAllText(Path.Combine(app.Root, "runtimes", "win-x64", "msvcp140.dll"), "fake");

        var error = await PrepareFails(app.Root);
        Assert.Contains("게시한 것과 달라", error.Message);
    }

    [Fact]
    public async Task DLL_하나가_빠지면_열지_않는다()
    {
        using var app = CopyOfNatives();
        File.Delete(Path.Combine(app.Root, "runtimes", "win-x64", "whisper.dll"));

        var error = await PrepareFails(app.Root);
        Assert.Contains("게시한 것과 달라", error.Message);
    }

    [Fact]
    public async Task runtimes_폴더가_없으면_안내한다()
    {
        using var app = new TempVault();

        var error = await PrepareFails(app.Root);
        Assert.Contains("runtimes 폴더", error.Message);
    }

    [Fact]
    public void 모델_경로_검사_없음_금고_안_크기()
    {
        using var vault = new TempVault();
        using var stt = new WhisperSpeechToText(AppContext.BaseDirectory, TimeSpan.FromMinutes(5));
        var outside = Path.Combine(Path.GetTempPath(), "TextBeanTests", Guid.NewGuid().ToString("N") + ".bin");

        Assert.Contains("없습니다", stt.ModelProblem(null, vault.Root));
        Assert.Contains("없습니다", stt.ModelProblem(Path.Combine(vault.Root, "없는.bin"), vault.Root));

        var inside = vault.WriteRaw("모델.bin", new byte[10]);
        Assert.Contains("금고 폴더 밖", stt.ModelProblem(inside, vault.Root));

        try
        {
            File.WriteAllBytes(outside, new byte[10]);
            Assert.Contains("이 모델 파일이 아닙니다", stt.ModelProblem(outside, vault.Root));

            using (var stream = File.Open(outside, FileMode.Open)) stream.SetLength(WhisperSpeechToText.ModelLength);
            Assert.Null(stt.ModelProblem(outside, vault.Root));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public async Task 크기가_같아도_내용이_다른_모델은_열지_않는다()
    {
        using var folder = new TempVault();
        var fake = Path.Combine(folder.Root, WhisperSpeechToText.ModelFileName);
        using (var stream = File.Create(fake)) stream.SetLength(WhisperSpeechToText.ModelLength);

        using var stt = new WhisperSpeechToText(AppContext.BaseDirectory, TimeSpan.FromMinutes(5));
        var error = await Assert.ThrowsAsync<VoiceUnavailableException>(() => stt.PrepareAsync(fake, CancellationToken.None));

        Assert.Contains("내용이 다릅니다", error.Message);
        Assert.False(stt.IsOpen);
    }

    [Theory]
    [InlineData(4.6, 384)]      // (4.6 + 3) / 30 × 1500 = 380 → 64 단위로 올림
    [InlineData(0.5, 192)]
    [InlineData(12.0, 768)]
    [InlineData(27.0, 1500)]    // 30초 이상은 Whisper 최대
    [InlineData(60.0, 1500)]
    public void 처리_창은_조각_길이_더하기_3초(double seconds, int expected)
        => Assert.Equal(expected, WhisperSpeechToText.AudioContextFor(seconds));

    [Fact]
    public void 스레드는_1에서_8()
        => Assert.InRange(WhisperSpeechToText.Threads, 1, 8);

    [Theory]
    [InlineData(unchecked((int)0x80070490), "마이크가 없습니다")]
    [InlineData(unchecked((int)0x80070005), "개인 정보 및 보안")]
    [InlineData(unchecked((int)0x88890004), "연결이 끊겼습니다")]
    [InlineData(unchecked((int)0x8889000A), "혼자 쓰고")]
    [InlineData(unchecked((int)0x80004005), "마이크를 열 수 없습니다")]
    public void 녹음_장치_오류_문구(int hresult, string expected)
    {
        Assert.Contains(expected, WasapiVoiceRecorder.Describe(new System.Runtime.InteropServices.COMException("x", hresult)));

        // 감싼 예외 안쪽까지 본다
        Assert.Contains(expected, WasapiVoiceRecorder.Describe(new InvalidOperationException("y", new System.Runtime.InteropServices.COMException("x", hresult))));
    }

    [Fact]
    public void 녹음_변환_48kHz_스테레오_실수를_16kHz_모노로()
    {
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
        var output = ConvertTone(WasapiVoiceRecorder.CreateConverter(format), 48000, 2, 0.5f,
                                 (bytes, i, v) => BitConverter.TryWriteBytes(bytes.AsSpan(i), v), 4);

        Assert.InRange(output.Length, 16000 - 64, 16000 + 64);
        Assert.InRange(Rms(output[4000..12000]), 0.33, 0.38);      // 0.5 / √2 = 0.354 — 두 채널 같은 소리라 섞어도 그대로
    }

    [Fact]
    public void 녹음_변환_Extensible_실수_형식도_실수로_읽는다()
    {
        // 장치 기본 형식은 보통 WaveFormatExtensible(하위 형식 = 실수)이다 — 정수로 읽으면 소리가 거의 0 이 된다
        var format = new NAudio.Wave.WaveFormatExtensible(48000, 32, 2);
        var output = ConvertTone(WasapiVoiceRecorder.CreateConverter(format), 48000, 2, 0.5f,
                                 (bytes, i, v) => BitConverter.TryWriteBytes(bytes.AsSpan(i), v), 4);

        Assert.InRange(Rms(output[4000..12000]), 0.33, 0.38);
    }

    [Fact]
    public void 녹음_변환_16kHz_16비트_모노는_값만_바꾼다()
    {
        var convert = WasapiVoiceRecorder.CreateConverter(new NAudio.Wave.WaveFormat(16000, 16, 1));
        var bytes = new byte[6];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), (short)16384);
        BitConverter.TryWriteBytes(bytes.AsSpan(2), (short)-32768);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (short)0);

        Assert.Equal([0.5f, -1f, 0f], convert(bytes));
    }

    [Fact]
    public void 녹음_변환_8비트는_쓸_수_없다고_알린다()
        => Assert.Throws<VoiceUnavailableException>(() => WasapiVoiceRecorder.CreateConverter(new NAudio.Wave.WaveFormat(16000, 8, 1)));

    /// 1초 440Hz 를 10ms 묶음으로 나눠 넣는다(장치가 주는 모양).
    private static float[] ConvertTone(WasapiVoiceRecorder.SampleConverter convert, int rate, int channels, float amplitude,
                                       Action<byte[], int, float> write, int bytesPerSample)
    {
        var output = new List<float>();
        var framesPerPacket = rate / 100;
        for (var packet = 0; packet < 100; packet++)
        {
            var bytes = new byte[framesPerPacket * channels * bytesPerSample];
            for (var f = 0; f < framesPerPacket; f++)
            {
                var t = packet * framesPerPacket + f;
                var value = amplitude * (float)Math.Sin(2 * Math.PI * 440 * t / rate);
                for (var c = 0; c < channels; c++) write(bytes, (f * channels + c) * bytesPerSample, value);
            }
            output.AddRange(convert(bytes));
        }
        return [.. output];
    }

    private static double Rms(float[] samples) => Math.Sqrt(samples.Sum(s => (double)s * s) / samples.Length);

    /// <summary>
    /// 실제 모델로 받아쓴다. TEXTBEAN_TEST_WHISPER_MODEL(모델 파일) · TEXTBEAN_TEST_WAV_DIR(s1~s4.wav · .txt, silence.wav, noise.wav —
    /// 검증 때 Windows 한국어 음성 Heami 로 만든 16kHz 모노)가 있을 때만 돈다. 각 WAV 를 조각 나누기 → 받아쓰기 순서로 넣는다.
    /// </summary>
    [WhisperModelFact]
    public async Task 실제_모델_합성_한국어_문장을_받아쓰고_무음_잡음은_조각이_없다()
    {
        var model = Environment.GetEnvironmentVariable(WhisperModelFactAttribute.ModelVariable)!;
        var wavs = Environment.GetEnvironmentVariable(WhisperModelFactAttribute.WavVariable)!;

        using var stt = new WhisperSpeechToText(AppContext.BaseDirectory, TimeSpan.FromMinutes(5));
        await stt.PrepareAsync(model, CancellationToken.None);
        Assert.True(stt.IsOpen);

        foreach (var name in new[] { "silence", "noise" })
            Assert.Empty(Chunks(Path.Combine(wavs, name + ".wav")));

        foreach (var name in new[] { "s1", "s2", "s3", "s4" })
        {
            var chunks = Chunks(Path.Combine(wavs, name + ".wav"));
            Assert.NotEmpty(chunks);

            var parts = new List<string>();
            foreach (var chunk in chunks) parts.Add(await stt.TranscribeAsync(chunk, CancellationToken.None));
            var text = string.Join(" ", parts);

            // 숫자 · 영어 표기는 바뀐다(「세 시」→「3시」 · 「깃허브」→「GitHub」) — 표기에 매이지 않는 낱말로 본다 [실측 — research 0-1절]
            var expected = File.ReadAllText(Path.Combine(wavs, name + ".txt"));
            var words = expected.Split([' ', ',', '.'], StringSplitOptions.RemoveEmptyEntries)
                                .Where(w => w.Length >= 3 && !w.Any(char.IsDigit) && w is not "깃허브")
                                .ToArray();
            var hits = words.Count(w => text.Contains(w[..2]));
            Assert.True(hits >= words.Length * 0.8, $"{name}: {hits}/{words.Length}");
        }
    }

    private static List<float[]> Chunks(string wavPath)
    {
        var bytes = File.ReadAllBytes(wavPath);
        var samples = new float[(bytes.Length - 44) / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(bytes, 44 + i * 2) / 32768f;

        var chunker = new SpeechChunker();
        var chunks = new List<float[]>();
        chunker.ChunkReady += chunks.Add;
        chunker.Push(samples);
        chunker.Flush();
        return chunks;
    }

    private static TempVault CopyOfNatives()
    {
        var app = new TempVault();
        var target = app.Dir("runtimes", "win-x64");
        foreach (var file in Directory.GetFiles(BuildNatives)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        return app;
    }

    /// 모델 경로는 아무 파일이나 — DLL 검사가 모델 확인보다 먼저다.
    private static async Task<VoiceUnavailableException> PrepareFails(string appFolder)
    {
        var anyFile = Path.Combine(appFolder, "모델 자리.bin");
        File.WriteAllBytes(anyFile, [1]);

        using var stt = new WhisperSpeechToText(appFolder, TimeSpan.FromMinutes(5));
        var error = await Assert.ThrowsAsync<VoiceUnavailableException>(() => stt.PrepareAsync(anyFile, CancellationToken.None));
        Assert.False(stt.IsOpen);
        return error;
    }
}

/// 실제 모델 · 시험 음성이 있을 때만 도는 시험 — 없으면 「건너뜀」으로 보인다(통과로 숨지 않게).
public sealed class WhisperModelFactAttribute : FactAttribute
{
    public const string ModelVariable = "TEXTBEAN_TEST_WHISPER_MODEL";
    public const string WavVariable = "TEXTBEAN_TEST_WAV_DIR";

    public WhisperModelFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable(ModelVariable)) || !Directory.Exists(Environment.GetEnvironmentVariable(WavVariable)))
            Skip = $"{ModelVariable} · {WavVariable} 가 없어 실제 모델 시험을 건너뜁니다.";
    }
}
