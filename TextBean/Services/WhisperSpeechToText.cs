using System.IO;
using System.Security.Cryptography;
using TextBean.Services.Interfaces;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace TextBean.Services;

/// <summary>
/// Whisper.net(whisper.cpp)으로 PC 안에서 받아쓴다 (D-179). 모델은 large-v3-turbo q5_0 하나만 받는다 — 크기 · SHA-256 을 고정해 둔다.
///
/// 금고 밖 파일 읽기는 이 클래스 한 곳만 한다 (PROHIBITED-CUSTOM-03 · 05 예외, D-188): 사용자가 고른 모델 파일과 exe 옆 DLL 4개를
/// 읽기만 한다. 금고 안의 모델 경로는 받지 않는다. 쓰기 · 지우기는 없다.
///
/// DLL 은 exe 옆 runtimes\win-x64\ 에서 Whisper.net 이 스스로 로드한다(라이브러리 자체 몫 — D-031 · D-093 부류, D-183).
/// 이 어셈블리는 P/Invoke 를 하지 않는다. 대신 처음 열기 전에 폴더에 고정 해시의 4개만 있는지 본다 — 바뀐 DLL 은 로드하지 않는다.
/// </summary>
public sealed class WhisperSpeechToText : ISpeechToText
{
    public const string ModelFileName = "ggml-large-v3-turbo-q5_0.bin";
    public const long ModelLength = 574_041_195;
    public const string ModelSha256 = "394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2";

    /// Whisper.net.Runtime 1.9.1 win-x64 [실측 — 패키지 원본과 빌드 출력이 같다]. 패키지를 올리면 VoiceNativeTests 가 실패해 알린다.
    public static IReadOnlyDictionary<string, string> NativeSha256 { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["ggml-base-whisper.dll"] = "e6c362421b6d5a5c1b52341f88d3d72764de29f05141748cc3faad2f6348063f",
        ["ggml-cpu-whisper.dll"] = "480a23afc6deea301c5cda66cf462fa2f2eb7ff9bca63b8f4f933a0e8dc5d4dd",
        ["ggml-whisper.dll"] = "2e0ac238c2bc178a5c1f09b3bde2421904b191bce1cc1c13dc64309ca3b9cb36",
        ["whisper.dll"] = "07ec2fac4946005ba7380f2c2e9ccfa3ad23007592d074aa698ed5df6bfb4938",
    };

    /// 처리 창 여유 (D-181). +1초는 앞 문장이 끝에 한 번 더 붙었다, +3초는 4문장에서 없었다 [실측].
    public const int AudioContextMarginSeconds = 3;

    private readonly string _appFolder;
    private readonly TimeSpan _idleClose;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idleTimer;
    private WhisperFactory? _factory;
    private ModelStamp? _verified;
    private bool _nativeChecked;
    private bool _disposed;

    public WhisperSpeechToText() : this(AppContext.BaseDirectory, TimeSpan.FromMinutes(5))
    {
    }

    /// <param name="appFolder">runtimes\win-x64\ 가 있는 폴더(exe 폴더).</param>
    /// <param name="idleClose">마지막 사용 뒤 모델을 닫기까지 — 열어 두면 약 0.8GB 를 쥔다 [실측].</param>
    public WhisperSpeechToText(string appFolder, TimeSpan idleClose)
    {
        _appFolder = appFolder;
        _idleClose = idleClose;
        _idleTimer = new Timer(_ => CloseIfIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string NativeFolder => Path.Combine(_appFolder, "runtimes", "win-x64");

    /// 지금 모델을 열어 두고 있는가 (시험용).
    public bool IsOpen => _factory is not null;

    /// 스레드 수 — 8 을 넘어도 빨라지지 않았다(10 · 12 · 16 모두 같음) [실측].
    public static int Threads => Math.Clamp(Environment.ProcessorCount / 2, 1, 8);

    /// 처리 창 = 조각 길이 + 3초 (Whisper 의 1500 = 30초, 64 단위). 기본(늘 30초)은 짧은 말도 10초 넘게 걸렸다 [실측].
    public static int AudioContextFor(double seconds)
        => Math.Min(1500, (int)Math.Ceiling((seconds + AudioContextMarginSeconds) / 30.0 * 1500 / 64) * 64);

    /// <summary>
    /// Whisper 가 조용하거나 알아듣기 힘든 소리(먼 말 · 웃음 · 물건 소리)에서 지어내는 방송 끝맺음 문장 (D-195).
    /// 소리 크기로도 Whisper 확률로도 가려지지 않았다 — 이 문장들은 확률이 0.85~0.97 로 진짜 말보다 높았다 [실측 — 노트북 녹음].
    /// 「감사합니다」 한마디는 회의에서 실제로 하는 말이라 넣지 않는다.
    /// </summary>
    public static IReadOnlyList<string> KnownHallucinations { get; } =
    [
        "이 시각 세계였습니다",        // 노트북 녹음 짧은 조각에서 여러 번 [실측]
        "다음 영상에서 만나요",        // 약한 잡음 10초에서 [실측 — research 0-1절]
        "시청해 주셔서 감사합니다",
        "구독과 좋아요 부탁드립니다",
    ];

    /// 구간 글자 전체가 목록 문장과 같은가(공백 · 문장부호 빼고). 문장 안에 섞여 있으면 진짜 말일 수 있어 버리지 않는다.
    public static bool IsKnownHallucination(string text)
    {
        var bare = Bare(text);
        return bare.Length > 0 && KnownHallucinations.Any(known => Bare(known) == bare);

        static string Bare(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
    }

    public string? ModelProblem(string? modelPath, string? vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            return $"음성 인식 모델 파일이 없습니다. {ModelFileName} 을 고르세요.";

        if (vaultRoot is not null && PathRules.IsInsideRoot(vaultRoot, Path.GetFullPath(modelPath)))
            return "모델 파일은 금고 폴더 밖에 두어야 합니다.";

        if (new FileInfo(modelPath).Length != ModelLength)
            return $"이 모델 파일이 아닙니다. {ModelFileName} 을 고르세요.";

        return null;
    }

    public async Task PrepareAsync(string modelPath, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopIdleTimer();
            await Task.Run(() => OpenAsync(modelPath, ct), ct);
        }
        finally
        {
            ArmIdleTimer();
            _gate.Release();
        }
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopIdleTimer();

            // 오래 조용해 쉬는 동안 닫혔으면 다시 연다 — 확인했던 파일이 바뀌었으면 다시 확인한다
            var model = _verified ?? throw new InvalidOperationException("PrepareAsync 를 먼저 불러야 합니다.");
            if (_factory is null) await Task.Run(() => OpenAsync(model.Path, ct), ct);

            return await Task.Run(async () =>
            {
                await using var processor = _factory!.CreateBuilder()
                    .WithLanguage("ko")
                    .WithThreads(Threads)
                    .WithAudioContextSize(AudioContextFor(samples.Length / (double)SpeechChunker.SampleRate))
                    .Build();

                var parts = new List<string>();
                await foreach (var segment in processor.ProcessAsync(samples, ct))
                {
                    var text = segment.Text.Trim();
                    if (text.Length > 0 && !IsKnownHallucination(text)) parts.Add(text);
                }
                return string.Join(" ", parts);
            }, ct);
        }
        finally
        {
            ArmIdleTimer();
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _idleTimer.Dispose();

        // 받아쓰는 중에 네이티브 모델을 풀면 안 된다. 조율기가 먼저 취소하고 기다린다 — 그래도 못 잡으면 프로세스 끝에 맡긴다
        if (!_gate.Wait(TimeSpan.FromSeconds(5))) return;
        try
        {
            _factory?.Dispose();
            _factory = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OpenAsync(string modelPath, CancellationToken ct)
    {
        if (!File.Exists(modelPath))
            throw new VoiceUnavailableException($"음성 인식 모델 파일이 없습니다. {ModelFileName} 을 고르세요.");

        if (!_nativeChecked)
        {
            CheckNatives();
            RuntimeOptions.LibraryPath = Path.Combine(_appFolder, "whisper.dll");   // 폴더 부분만 쓴다 — 이 폴더 아래 runtimes\win-x64
            RuntimeOptions.RuntimeLibraryOrder = [RuntimeLibrary.Cpu];
            _nativeChecked = true;
        }

        var stamp = ModelStamp.Of(modelPath);
        if (_verified != stamp)
        {
            await VerifyModelAsync(modelPath, ct);
            _factory?.Dispose();
            _factory = null;
            _verified = stamp;
        }

        if (_factory is not null) return;

        try
        {
            _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseFlashAttention = true });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("voice-open", modelPath, ex);
            throw new VoiceUnavailableException(
                "음성 인식 엔진을 열 수 없습니다. Microsoft Visual C++ 재배포 패키지(x64)가 설치돼 있는지 확인하세요.", ex);
        }
    }

    private void CheckNatives()
    {
        var folder = NativeFolder;
        if (!Directory.Exists(folder))
            throw new VoiceUnavailableException("음성 인식 파일이 없습니다. TextBean.exe 옆에 runtimes 폴더가 있어야 합니다.");

        var entries = Directory.GetFileSystemEntries(folder);
        var intact = entries.Length == NativeSha256.Count
                  && entries.All(path => NativeSha256.TryGetValue(Path.GetFileName(path), out var expected)
                                      && File.Exists(path)
                                      && Sha256Of(path) == expected);
        if (intact) return;

        AppLog.Error("voice-native-mismatch", folder, null);
        throw new VoiceUnavailableException("음성 인식 파일이 게시한 것과 달라 열지 않았습니다. runtimes 폴더를 다시 게시한 것으로 바꾸세요.");
    }

    private static async Task VerifyModelAsync(string modelPath, CancellationToken ct)
    {
        await using var stream = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        if (stream.Length != ModelLength)
            throw new VoiceUnavailableException($"이 모델 파일이 아닙니다. {ModelFileName} 을 고르세요.");

        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        if (hash == ModelSha256) return;

        AppLog.Error("voice-model-mismatch", modelPath, null);
        throw new VoiceUnavailableException($"모델 파일 내용이 다릅니다(받다가 깨졌거나 다른 파일). {ModelFileName} 을 다시 받으세요.");
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private void StopIdleTimer() => _idleTimer.Change(Timeout.Infinite, Timeout.Infinite);

    private void ArmIdleTimer()
    {
        if (!_disposed) _idleTimer.Change(_idleClose, Timeout.InfiniteTimeSpan);
    }

    private void CloseIfIdle()
    {
        // 쓰는 중이면 그 사용이 끝날 때 타이머를 다시 건다
        if (!_gate.Wait(0)) return;
        try
        {
            _factory?.Dispose();
            _factory = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// 확인한 파일이 그대로인지 — 경로 · 크기 · 고친 시각.
    private sealed record ModelStamp(string Path, long Length, DateTime WriteUtc)
    {
        public static ModelStamp Of(string path)
        {
            var info = new FileInfo(path);
            return new ModelStamp(info.FullName, info.Length, info.LastWriteTimeUtc);
        }
    }
}
