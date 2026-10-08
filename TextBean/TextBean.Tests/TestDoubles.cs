using TextBean.Models;
using TextBean.Services.Interfaces;
using TextBean.ViewModels;

namespace TextBean.Tests;

public sealed class FakeClipboard : IClipboardService
{
    public string? Copied { get; private set; }
    public void Copy(string text) => Copied = text;

    /// 서식째 복사한 마지막 것 (D-126). 글자는 Copied 에도 남는다.
    public ClipboardPayload? CopiedPayload { get; private set; }
    public void Copy(ClipboardPayload payload) { CopiedPayload = payload; Copied = payload.Text; }

    /// 정리할 때 돌린다 — 정리가 키 지우기보다 먼저인지 뒤인지 본다.
    public Action? DuringClear;

    public void ClearIfOurs()
    {
        DuringClear?.Invoke();
        Copied = null;
    }
    public void Dispose() { }
}

public sealed class FakeDialogs : IDialogService
{
    public int ErrorCount { get; private set; }
    public int ConfirmCount { get; private set; }
    public string? LastErrorTitle { get; private set; }
    public string? LastErrorMessage { get; private set; }

    /// null 이면 취소한 것으로 본다.
    public string? PickDocumentResult;
    public int PickDocumentCount { get; private set; }

    /// null 이면 initial 을 그대로 돌려준다 (취소 아님).
    public string? PromptTextResult;

    /// true 면 금고 폴더 선택 창에서 취소한 것으로 본다. 재지정 창이 떴는지를 세는 시험용.
    public bool CancelPickFolder;
    public int PickFolderCount { get; private set; }

    /// 주면 폴더 선택 창이 이것을 돌려준다(다른 금고 폴더). 안 주면 제안 경로를 그대로.
    public string? PickFolderResult;

    public string? PickFolder(string suggestedPath, string title)
    {
        if (SuppressedNow()) return null;

        PickFolderCount++;
        return CancelPickFolder ? null : PickFolderResult ?? suggestedPath;
    }

    public string? PromptText(string title, string initial) => SuppressedNow() ? null : PromptTextResult ?? initial;

    /// 키 입력창이 차례로 돌려줄 값. 비어 있으면 취소한 것으로 본다.
    public Queue<KeyEntry?> KeyEntries { get; } = new();
    public List<string> KeyPromptTitles { get; } = [];
    public List<string> KeyPromptMessages { get; } = [];
    public List<string> ConfirmMessages { get; } = [];
    public List<string> ConfirmTitles { get; } = [];

    /// false 면 확인 창에서 취소한 것으로 본다.
    public bool ConfirmResult = true;

    /// <summary>
    /// 확인 · 오류 창이 "떠 있는 동안" 돌린다 — 그 사이 들어온 요청(✕ 연타 · 트레이 종료)을 시험한다.
    /// 도는 동안 <see cref="IsShowing"/> 이 참이다. 진짜 서비스가 창을 띄운 동안 그런 것과 같다.
    /// </summary>
    public Action? DuringConfirm;
    public Action? DuringError;

    public KeyEntry? PromptKey(string title, string message, bool confirm)
    {
        if (SuppressedNow()) return null;

        KeyPromptTitles.Add(title);
        KeyPromptMessages.Add(message);
        return KeyEntries.Count > 0 ? KeyEntries.Dequeue() : null;
    }

    public string? PickDocument(string suggestedFolder, string title)
    {
        if (SuppressedNow()) return null;

        PickDocumentCount++;
        return PickDocumentResult;
    }

    /// null 이면 취소한 것으로 본다.
    public string? PickImageFileResult;
    public int PickImageFileCount { get; private set; }

    public string? PickImageFile()
    {
        if (SuppressedNow()) return null;

        PickImageFileCount++;
        return PickImageFileResult;
    }

    /// null 이면 취소한 것으로 본다.
    public string? PickModelFileResult;
    public int PickModelFileCount { get; private set; }

    public string? PickModelFile()
    {
        if (SuppressedNow()) return null;

        PickModelFileCount++;
        return PickModelFileResult;
    }

    public bool Confirm(string title, string message)
    {
        if (SuppressedNow()) return false;

        ConfirmCount++;
        ConfirmTitles.Add(title);
        ConfirmMessages.Add(message);
        WhileShowing(DuringConfirm);
        return ConfirmResult;
    }

    /// 첫 ✕ 안내도 확인 창으로 센다 — 제목 "트레이로 숨깁니다", 결과는 ConfirmResult.
    public bool ConfirmHideToTray() => Confirm("트레이로 숨깁니다", "");

    public void Error(string title, string message)
    {
        if (SuppressedNow()) return;

        ErrorCount++;
        LastErrorTitle = title;
        LastErrorMessage = message;
        WhileShowing(DuringError);
    }

    /// 시험이 "대화상자가 떠 있다"를 세운다 — 실제 창은 띄우지 않는다.
    public bool IsShowing { get; set; }

    /// 억제 중에 들어와 띄우지 않은 대화상자 수. 억제 중에는 위의 횟수(ConfirmCount 등)가 늘지 않는다.
    public int SuppressedCount { get; private set; }
    public int SuppressDepth { get; private set; }

    /// 억제를 몇 번 걸었나 — 셸을 거친 억제가 실제로 이 서비스에 닿는지 본다.
    public int SuppressCalls { get; private set; }

    public IDisposable Suppress()
    {
        SuppressCalls++;
        SuppressDepth++;
        return new SuppressScope(this);
    }

    private sealed class SuppressScope(FakeDialogs owner) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            owner.SuppressDepth--;
        }
    }

    private bool SuppressedNow()
    {
        if (SuppressDepth == 0) return false;
        SuppressedCount++;
        return true;
    }

    private void WhileShowing(Action? during)
    {
        if (during is null) return;

        var was = IsShowing;
        IsShowing = true;
        try { during(); }
        finally { IsShowing = was; }
    }
}

/// <summary>
/// 진짜 알림 영역 아이콘을 만들지 않는다 — 시험이 사용자 트레이에 아이콘을 올리면 안 된다 (D-103).
/// 메뉴 클릭은 Raise… 로 흉내낸다.
/// </summary>
/// 마이크 대신 — 시험이 소리를 밀어 넣는다(실제 녹음은 하지 않는다, plan R-8).
public sealed class FakeRecorder : IVoiceRecorder
{
    public event Action<float[]>? Samples;
    public event Action<VoiceUnavailableException>? Failed;

    public int StartCount { get; private set; }
    public int StopCount { get; private set; }
    public bool IsRecording { get; private set; }
    public bool Disposed { get; private set; }

    /// 주면 Start 가 이것을 던진다(마이크 없음 등).
    public VoiceUnavailableException? StartError;

    public void Start()
    {
        if (StartError is not null) throw StartError;
        StartCount++;
        IsRecording = true;
    }

    public void Stop()
    {
        StopCount++;
        IsRecording = false;
    }

    public void Push(float[] samples) => Samples?.Invoke(samples);

    public void Fail(string message)
    {
        IsRecording = false;
        Failed?.Invoke(new VoiceUnavailableException(message));
    }

    public void Dispose()
    {
        Disposed = true;
        IsRecording = false;
    }
}

/// 모델 대신 — 조각마다 정해 둔 글자를 돌려준다. 준비 · 받아쓰기를 붙잡아 「준비 중」 · 「받아쓰는 중」을 결정적으로 만든다.
public sealed class FakeSpeechToText : ISpeechToText
{
    public const string GoodPath = @"C:\모델\ggml-large-v3-turbo-q5_0.bin";

    public int PrepareCount;
    public string? PreparedPath;
    public TaskCompletionSource? PrepareGate;
    public Exception? PrepareError;

    /// 차례로 돌려줄 글자. 비면 「조각N」.
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> Texts = new();
    public TaskCompletionSource? TranscribeGate;
    public Exception? TranscribeError;
    public readonly System.Collections.Concurrent.ConcurrentQueue<int> ChunkLengths = new();
    public bool Disposed;

    public string? ModelProblem(string? modelPath, string? vaultRoot)
        => modelPath == GoodPath ? null : "음성 인식 모델 파일이 없습니다.";

    public async Task PrepareAsync(string modelPath, CancellationToken ct)
    {
        Interlocked.Increment(ref PrepareCount);
        PreparedPath = modelPath;
        if (PrepareGate is { } gate) await gate.Task.WaitAsync(ct);
        if (PrepareError is { } error) throw error;
    }

    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct)
    {
        ChunkLengths.Enqueue(samples.Length);
        if (TranscribeGate is { } gate) await gate.Task.WaitAsync(ct);
        if (TranscribeError is { } error) throw error;
        return Texts.TryDequeue(out var text) ? text : $"조각{ChunkLengths.Count}";
    }

    public void Dispose() => Disposed = true;
}

public sealed class FakeTrayIcon : ITrayIcon
{
    public event EventHandler? OpenRequested;
    public event EventHandler? LockRequested;
    public event EventHandler? EnterKeyRequested;
    public event EventHandler? ExitRequested;

    public int ShowCount { get; private set; }
    public int DisposeCount { get; private set; }
    public string? ToolTip { get; private set; }
    public bool? HasKey { get; private set; }

    /// 지나간 툴팁 전부 — "키 확인 중"처럼 잠깐 지나가는 상태를 본다.
    public List<string> ToolTips { get; } = [];

    public void Show() => ShowCount++;

    public void Update(string toolTip, bool hasKey)
    {
        ToolTip = toolTip;
        HasKey = hasKey;
        ToolTips.Add(toolTip);
    }

    public void RaiseOpen() => OpenRequested?.Invoke(this, EventArgs.Empty);
    public void RaiseLock() => LockRequested?.Invoke(this, EventArgs.Empty);
    public void RaiseEnterKey() => EnterKeyRequested?.Invoke(this, EventArgs.Empty);
    public void RaiseExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose() => DisposeCount++;
}

/// 진짜 전원 알림(SystemEvents)을 구독하지 않는다. 절전 진입은 RaiseSuspending 으로 흉내낸다.
public sealed class FakePowerEvents : IPowerEvents
{
    public event EventHandler? Suspending;

    public int DisposeCount { get; private set; }

    public void RaiseSuspending() => Suspending?.Invoke(this, EventArgs.Empty);

    public void Dispose() => DisposeCount++;
}

/// <summary>
/// 진짜 탐색기를 띄우지 않는다. 무엇을 열려고 했는지만 적어 둔다.
/// 진짜 ExplorerLauncher 는 창을 띄우므로 자동 테스트 밖에서 실측으로 검증한다(scratchpad/explorer-verify).
/// </summary>
public sealed class FakeExplorerLauncher : IExplorerLauncher
{
    public List<(string Kind, string Path)> Calls { get; } = [];

    /// false 면 "찾지 못함" — 트리를 새로 읽기 전에 밖에서 지워진 항목을 흉내낸다.
    public bool Result { get; set; } = true;

    /// 셸 호출이 던지는 경우(DllNotFoundException 등)를 흉내낸다.
    public Exception? Throw { get; set; }

    public Task<bool> OpenFolderAsync(string folderFullPath) => Record("folder", folderFullPath);

    public Task<bool> RevealAsync(string itemFullPath) => Record("reveal", itemFullPath);

    // 진짜는 Task.Run 안에서 던지므로 동기 throw 가 아니라 실패한 Task 로 돌려준다
    private Task<bool> Record(string kind, string path)
    {
        Calls.Add((kind, path));
        return Throw is null ? Task.FromResult(Result) : Task.FromException<bool>(Throw);
    }
}

/// <summary>
/// 저장을 원하는 지점에서 멈춰 세운다. "저장이 도는 도중"을 테스트가 결정적으로 만들 수 있다 —
/// 실제 파일 I/O 는 캐시 때문에 동기로 끝나기도 해서 타이밍에 기대면 간헐 실패가 된다 (LL-079).
/// </summary>
public sealed class GatedDocumentStore(IDocumentStore inner) : IDocumentStore
{
    private TaskCompletionSource? _gate;
    private bool _gateReads;
    private int _readsToGate;
    private TaskCompletionSource _blocked = new();

    /// 이후 SaveAsync 를 붙잡아 둔다.
    public void Close() => _gate = new TaskCompletionSource();

    /// 이후 LoadAsync 를 전부 붙잡아 둔다 — 검색이 도는 중을 결정적으로 만든다.
    public void CloseReads() => CloseReads(int.MaxValue);

    /// <summary>
    /// 앞의 <paramref name="count"/> 번 읽기만 붙잡는다.
    /// 전부 막으면 "먼저 시작한 검색이 멈춘 사이 뒤 검색이 끝난다"를 만들 수 없다 — 둘 다 멈춘다.
    /// </summary>
    public void CloseReads(int count)
    {
        _gate = new TaskCompletionSource();
        _blocked = new TaskCompletionSource();
        _gateReads = true;
        _readsToGate = count;
    }

    /// <summary>
    /// 읽기가 실제로 게이트에 걸릴 때까지 기다린다.
    /// 이게 없으면 어느 쪽 검색이 슬롯을 먹는지가 경쟁이라 테스트가 간헐적으로 멈춘다 —
    /// ScanAsync 가 Task.Run 이라 두 검색이 같은 시점에 대기 상태로 들어간다 [실측].
    /// </summary>
    public Task ReadBlocked => _blocked.Task;

    public void Open() { _gate?.TrySetResult(); _gate = null; _gateReads = false; _readsToGate = 0; }

    public Task CreateAsync(string fullPath) => inner.CreateAsync(fullPath);

    public async Task<DocumentReadResult> LoadAsync(string fullPath)
    {
        if (_gateReads && _gate is { } gate && _readsToGate > 0)
        {
            _readsToGate--;
            _blocked.TrySetResult();
            await gate.Task;
        }
        return await inner.LoadAsync(fullPath);
    }

    public Task SaveAsync(string fullPath, string text) => SaveAsync(fullPath, DocumentBody.Plain(text), binding: null);

    /// 저장마다 부른다 — "저장하는 사이 계속 입력이 들어오는" 경우를 흉내낸다.
    public Action? DuringSave;

    /// <summary>
    /// 첫 저장을 둘째 저장이 끝날 때까지(최대 1초) 붙잡는다 — "먼저 시작한 자동 저장이 늦게 끝나는" 경우 (U-4).
    /// 저장이 직렬화돼 있으면 둘째가 시작하지 못해 1초 뒤 풀린다.
    /// </summary>
    public bool HoldFirstSaveUntilSecondEnds;

    private readonly TaskCompletionSource _secondSaveEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _saveCalls;

    public async Task SaveAsync(string fullPath, DocumentBody body, DocumentKeyBinding? binding)
    {
        var call = Interlocked.Increment(ref _saveCalls);
        DuringSave?.Invoke();
        if (_gate is not null) await _gate.Task;
        if (HoldFirstSaveUntilSecondEnds && call == 1)
            await Task.WhenAny(_secondSaveEnded.Task, Task.Delay(TimeSpan.FromSeconds(1)));

        try
        {
            await inner.SaveAsync(fullPath, body, binding);
        }
        finally
        {
            if (call == 2) _secondSaveEnded.TrySetResult();
        }
    }

    public async Task SavePlainAsync(string fullPath, string text, PlainTextFormat format)
    {
        DuringSave?.Invoke();
        if (_gate is not null) await _gate.Task;
        await inner.SavePlainAsync(fullPath, text, format);
    }

    public Task<IReadOnlyList<DocumentHeader?>> ReadHeadersAsync(IReadOnlyList<string> fullPaths)
        => inner.ReadHeadersAsync(fullPaths);

    public Task<IReadOnlyList<DocumentKeyState>> ClassifyAsync(IReadOnlyList<string> fullPaths)
        => inner.ClassifyAsync(fullPaths);

    public void CaptureOpenSnapshot(string fullPath) => inner.CaptureOpenSnapshot(fullPath);

    public string? SnapshotPathIfExists(string fullPath) => inner.SnapshotPathIfExists(fullPath);
}

/// <summary>
/// 실제 팩터리는 WPF DispatcherTimer 를 물린다 — 테스트 스레드에서는 절대 돌지 않는다.
/// 여기서는 발화시킬 수 있는 가짜 타이머를 물리고, 만든 타이머를 꺼내 볼 수 있게 둔다.
/// </summary>
public sealed class FakeEditorFactory(IDocumentStore store, IDialogService dialogs) : IEditorFactory
{
    public List<FakeAutoSaveTimer> Timers { get; } = [];
    public List<EditorViewModel> Created { get; } = [];

    public EditorViewModel Create()
    {
        var timer = new FakeAutoSaveTimer();
        Timers.Add(timer);
        var editor = new EditorViewModel(store, new FakeClipboard(), dialogs, timer);
        Created.Add(editor);
        return editor;
    }
}

/// 자동 저장 타이머를 테스트가 직접 발화시킬 수 있게 한다.
public sealed class FakeAutoSaveTimer : IAutoSaveTimer
{
    public int RestartCount { get; private set; }
    public bool IsRunning { get; private set; }

    public event EventHandler? Elapsed;

    public void Restart() { RestartCount++; IsRunning = true; }

    public void Stop() => IsRunning = false;

    /// 예약된 저장이 없으면 아무 일도 없어야 한다 — 실제 타이머와 같은 규칙이다.
    public void Fire()
    {
        if (!IsRunning) return;
        IsRunning = false;
        Elapsed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() { }
}

/// <summary>
/// 글자만 든 본문으로 암호화하는 시험용 줄임. 코덱은 DocumentBody 를 받는다 (D-123) — 시험 대부분은 글자만 본다.
/// </summary>
public static class CodecTestExtensions
{
    public static byte[] EncryptForNew(this IDocumentCodec codec, string text) => codec.EncryptForNew(DocumentBody.Plain(text));

    public static byte[] EncryptReplacing(this IDocumentCodec codec, string text, ReadOnlySpan<byte> existingHeader, DocumentKeyBinding? binding)
        => codec.EncryptReplacing(DocumentBody.Plain(text), existingHeader, binding);

    public static byte[] EncryptFor(this IDocumentCodec codec, string text, DocumentKeyBinding binding)
        => codec.EncryptFor(DocumentBody.Plain(text), binding);
}
