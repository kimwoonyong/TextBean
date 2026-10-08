using System.Diagnostics;
using System.Threading.Channels;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Threading;
using TextBean.Services;
using TextBean.Services.Interfaces;
using TextBean.ViewModels;

namespace TextBean.Views.Platform;

/// <summary>
/// 음성 입력 조율 (D-180). 마이크 → 조각 나누기 → 순서 큐(한 번에 하나, 순서 유지) → 받아쓰기 → 시작한 탭 본문의 「넣을 자리」.
/// 누르면 곧바로 듣고, 모델 확인 · 열기는 뒤에서 한다 — 그 사이 생긴 조각은 큐에서 기다린다 (plan R-4).
/// 본문을 직접 만지므로 Views/Platform 에 둔다(D-013). 상태 변화는 UI 스레드에서 알린다.
/// 소리 배열은 쓴 뒤 지우고, 받아쓴 글자는 로그 · 오류 문구에 넣지 않는다 (D-188).
/// </summary>
public sealed class DictationController : IDictation
{
    private const string Title = "음성 입력";

    private readonly IVoiceRecorder _recorder;
    private readonly ISpeechToText _speech;
    private readonly IAppSettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly Func<EditorViewModel, TextBoxBase?> _findBody;
    private readonly Func<string?> _vaultRoot;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _clock;
    private Session? _session;
    private bool _starting;
    private bool _disposed;

    public DictationController(IVoiceRecorder recorder, ISpeechToText speech, IAppSettingsService settings, IDialogService dialogs,
                               Func<EditorViewModel, TextBoxBase?> findBody, Func<string?> vaultRoot)
    {
        _recorder = recorder;
        _speech = speech;
        _settings = settings;
        _dialogs = dialogs;
        _findBody = findBody;
        _vaultRoot = vaultRoot;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // 상태 줄 시계 — 듣는 동안만 돈다
        _clock = new DispatcherTimer(DispatcherPriority.Background, _dispatcher) { Interval = TimeSpan.FromSeconds(1) };
        _clock.Tick += (_, _) => Raise();

        recorder.Samples += OnSamples;
        recorder.Failed += OnRecorderFailed;
    }

    public DictationState State => _session?.State ?? DictationState.Idle;
    public bool IsPreparing => _session?.Preparing ?? false;
    public int Pending => _session?.Pending ?? 0;
    public TimeSpan Elapsed => _session?.Clock.Elapsed ?? TimeSpan.Zero;
    public object? Target => _session?.Tab;

    public event EventHandler? StateChanged;

    public bool CanStart(object? tab)
        => !_disposed && tab is EditorViewModel { HasDocument: true, IsReadOnly: false } editor && _findBody(editor) is not null;

    public async Task StartAsync(object tab)
    {
        if (_session is not null || _starting || !CanStart(tab)) return;

        var editor = (EditorViewModel)tab;
        _starting = true;
        try
        {
            var modelPath = await EnsureModelPathAsync();

            // 고르는 동안 탭이 닫혔거나 읽기 전용이 됐을 수 있다
            if (modelPath is null || _session is not null || !CanStart(editor)) return;

            var session = new Session(editor, Anchor.At(_findBody(editor)!), () => _dispatcher.BeginInvoke(Raise));
            _session = session;
            try
            {
                _recorder.Start();
            }
            catch (VoiceUnavailableException ex)
            {
                _session = null;
                session.Anchor.Detach();
                _dialogs.Error(Title, ex.Message);
                return;
            }

            editor.Closed += OnTargetClosed;
            session.Clock.Start();
            _clock.Start();
            session.Prepare = _speech.PrepareAsync(modelPath, session.Token);
            session.Worker = Task.Run(() => RunAsync(session));
            Raise();
        }
        finally
        {
            _starting = false;
        }
    }

    public void Stop()
    {
        if (_session is not { State: DictationState.Listening } session) return;

        _recorder.Stop();
        session.EndListening();
        session.State = DictationState.Finishing;
        session.Clock.Stop();
        _clock.Stop();
        Raise();
    }

    public void Cancel()
    {
        if (_session is not { } session) return;

        _session = null;
        _recorder.Stop();
        session.Abort();
        Detach(session);
        Raise();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Cancel();
        _recorder.Samples -= OnSamples;
        _recorder.Failed -= OnRecorderFailed;
        _recorder.Dispose();

        // 받아쓰는 중이면 그 조각이 끝날 때까지 기다린다(최대 5초) — 네이티브 모델을 쓰는 중에 풀지 않는다
        _speech.Dispose();
    }

    private async Task<string?> EnsureModelPathAsync()
    {
        var root = _vaultRoot();
        var path = _settings.Current.VoiceModelPath;
        if (_speech.ModelProblem(path, root) is null) return path;

        var picked = _dialogs.PickModelFile();
        if (picked is null) return null;

        if (_speech.ModelProblem(picked, root) is { } problem)
        {
            _dialogs.Error(Title, problem);
            return null;
        }

        _settings.Current.VoiceModelPath = picked;
        await _settings.SaveAsync();
        return picked;
    }

    /// 뒤에서 돈다 — 모델 준비를 기다린 뒤 조각을 차례로 받아써 UI 스레드에서 넣는다.
    private async Task RunAsync(Session session)
    {
        try
        {
            await session.Prepare!;
            await OnUiAsync(() =>
            {
                session.Preparing = false;
                Raise();
            });

            await foreach (var chunk in session.Queue.Reader.ReadAllAsync(session.Token))
            {
                string text;
                try
                {
                    text = await _speech.TranscribeAsync(chunk, session.Token);
                }
                finally
                {
                    Array.Clear(chunk);
                    session.ChunkDone();
                }

                await OnUiAsync(() => Insert(session, text));
            }

            await OnUiAsync(() => Finish(session));
        }
        catch (OperationCanceledException) when (session.Token.IsCancellationRequested)
        {
            // 취소 — 남은 조각은 버렸다
        }
        catch (VoiceUnavailableException ex)
        {
            await OnUiAsync(() => Fail(session, ex.Message));
        }
        catch (Exception ex)
        {
            AppLog.Error("voice-transcribe", null, ex);
            await OnUiAsync(() => Fail(session, "받아쓰기에 실패해 음성 입력을 멈췄습니다."));
        }
    }

    private void Insert(Session session, string text)
    {
        if (!ReferenceEquals(_session, session)) return;

        // 닫히거나 읽기 전용이 된 탭에는 넣지 않는다 — 남은 조각도 버린다
        if (!session.Tab.HasDocument || session.Tab.IsReadOnly)
        {
            Cancel();
            return;
        }

        if (text.Length > 0) session.Anchor.Insert(text);
        Raise();
    }

    private void Finish(Session session)
    {
        if (!ReferenceEquals(_session, session)) return;

        _session = null;
        Detach(session);
        Raise();
    }

    private void Fail(Session session, string message)
    {
        if (!ReferenceEquals(_session, session)) return;

        Cancel();
        _dialogs.Error(Title, message);
    }

    private void Detach(Session session)
    {
        session.Tab.Closed -= OnTargetClosed;
        session.Anchor.Detach();
        session.Clock.Stop();
        _clock.Stop();
    }

    private void OnSamples(float[] samples) => Volatile.Read(ref _session)?.Push(samples);

    /// 녹음 중 장치가 빠지는 등 — 남은 조각은 넣고 멈춘 뒤 알린다.
    private void OnRecorderFailed(VoiceUnavailableException error) => _dispatcher.BeginInvoke(() =>
    {
        if (_session is not { State: DictationState.Listening }) return;

        Stop();
        _dialogs.Error(Title, error.Message);
    });

    private void OnTargetClosed(object? sender, EventArgs e) => Cancel();

    private Task OnUiAsync(Action action) => _dispatcher.InvokeAsync(action).Task;

    private void Raise() => StateChanged?.Invoke(this, EventArgs.Empty);

    /// 한 번 켠 음성 입력. 취소되면 버려지고, 늦게 끝난 뒤처리는 자기가 지금 것인지 보고 아무것도 안 한다.
    private sealed class Session
    {
        private readonly Lock _gate = new();
        private readonly SpeechChunker _chunker = new();
        private readonly CancellationTokenSource _cancel = new();
        private readonly Action _changed;
        private bool _listening = true;
        private int _pending;

        public Session(EditorViewModel tab, Anchor anchor, Action changed)
        {
            Tab = tab;
            Anchor = anchor;
            _changed = changed;
            _chunker.ChunkReady += OnChunk;
        }

        public EditorViewModel Tab { get; }
        public Anchor Anchor { get; }
        public DictationState State { get; set; } = DictationState.Listening;
        public bool Preparing { get; set; } = true;
        public int Pending => Volatile.Read(ref _pending);
        public Stopwatch Clock { get; } = new();
        public CancellationToken Token => _cancel.Token;
        public Channel<float[]> Queue { get; } = Channel.CreateUnbounded<float[]>(new UnboundedChannelOptions { SingleReader = true });
        public Task? Prepare { get; set; }
        public Task? Worker { get; set; }

        /// 녹음 스레드에서 온다.
        public void Push(float[] samples)
        {
            lock (_gate)
            {
                if (_listening) _chunker.Push(samples);
            }
            Array.Clear(samples);
        }

        /// 듣기를 끝내고 말하던 조각까지 큐에 넣는다.
        public void EndListening()
        {
            lock (_gate)
            {
                if (!_listening) return;
                _listening = false;
                _chunker.Flush();
            }
            Queue.Writer.TryComplete();
        }

        public void Abort()
        {
            _cancel.Cancel();
            lock (_gate)
            {
                _listening = false;
                _chunker.Reset();
            }
            Queue.Writer.TryComplete();
            while (Queue.Reader.TryRead(out var chunk)) Array.Clear(chunk);
        }

        public void ChunkDone()
        {
            Interlocked.Decrement(ref _pending);
            _changed();
        }

        private void OnChunk(float[] chunk)
        {
            Interlocked.Increment(ref _pending);
            if (!Queue.Writer.TryWrite(chunk))
            {
                Interlocked.Decrement(ref _pending);
                Array.Clear(chunk);
            }
            _changed();
        }
    }

    /// <summary>
    /// 받아쓴 글자를 넣을 자리 (D-184). 시작 때 커서(선택이 있으면 선택 끝)에서 시작해, 넣을 때마다 넣은 글자 끝으로 간다.
    /// 사용자의 커서 · 선택은 옮기지 않는다 — 커서가 바로 그 자리에 있었을 때만 넣은 글자 뒤로 따라간다 (plan R-3).
    /// 조각 하나 = 실행취소 한 번. 앞 글자가 공백 · 줄바꿈이 아니면 한 칸 띄운다.
    /// </summary>
    private abstract class Anchor
    {
        public static Anchor At(TextBoxBase body) => body switch
        {
            RichTextBox rich => new RichAnchor(rich),
            TextBox plain => new PlainAnchor(plain),
            _ => throw new NotSupportedException(body.GetType().Name)
        };

        public abstract void Insert(string text);

        public virtual void Detach()
        {
        }

        protected static string Spaced(char? before, string text)
            => before is { } c && !char.IsWhiteSpace(c) ? " " + text : text;
    }

    /// 서식 본문 — 앞쪽 붙음(Forward) 자리는 그 자리에 넣은 글자 뒤로 밀린다. 사용자가 다른 곳을 고쳐도 내용을 따라간다.
    private sealed class RichAnchor : Anchor
    {
        private readonly RichTextBox _box;
        private TextPointer _at;

        public RichAnchor(RichTextBox box)
        {
            _box = box;
            _at = Forward(box.Selection.End);
        }

        public override void Insert(string text)
        {
            // 문서를 통째로 다시 불러왔으면 옛 자리는 없다 — 끝에 잇는다
            if (!_box.Document.ContentStart.IsInSameDocument(_at)) _at = Forward(_box.Document.ContentEnd);
            if (!_at.IsAtInsertionPosition) _at = Forward(_at.GetInsertionPosition(LogicalDirection.Forward));

            var selection = _box.Selection;
            var caretHere = selection.IsEmpty && selection.Start.CompareTo(_at) == 0;
            var before = _at.Paragraph is { } paragraph ? new TextRange(paragraph.ContentStart, _at).Text : "";

            // 선택이 넣을 자리에서 끝나면 선택이 넣은 글자까지 늘어났다 [실측 — DictationTests]. 끝은 뒤쪽 붙음, 시작은 앞쪽 붙음으로 잡아
            // 넣은 뒤 같은 글자를 다시 고른다
            var keepStart = selection.Start.GetPositionAtOffset(0, LogicalDirection.Forward)!;
            var keepEnd = selection.End.GetPositionAtOffset(0, LogicalDirection.Backward)!;

            _box.BeginChange();
            try
            {
                _at.InsertTextInRun(Spaced(before.Length > 0 ? before[^1] : null, text));
            }
            finally
            {
                _box.EndChange();
            }

            if (caretHere) _box.Selection.Select(_at, _at);
            else _box.Selection.Select(keepStart, keepEnd);
        }

        private static TextPointer Forward(TextPointer at) => at.GetPositionAtOffset(0, LogicalDirection.Forward)!;
    }

    /// .txt 본문 — 자리는 글자 위치. 사용자가 그 앞을 고치면 변경 목록으로 보정한다.
    private sealed class PlainAnchor : Anchor
    {
        private readonly TextBox _box;
        private int _at;
        private bool _inserting;

        public PlainAnchor(TextBox box)
        {
            _box = box;
            _at = box.SelectionStart + box.SelectionLength;
            box.TextChanged += OnTextChanged;
        }

        public override void Insert(string text)
        {
            _at = Math.Clamp(_at, 0, _box.Text.Length);
            var piece = Spaced(_at > 0 ? _box.Text[_at - 1] : null, text);

            var start = _box.SelectionStart;
            var length = _box.SelectionLength;
            var caretHere = length == 0 && start == _at;
            var at = _at;

            _inserting = true;
            try
            {
                _box.BeginChange();
                try
                {
                    _box.Select(at, 0);
                    _box.SelectedText = piece;
                }
                finally
                {
                    _box.EndChange();
                }
            }
            finally
            {
                _inserting = false;
            }

            _at = at + piece.Length;

            // 사용자 선택을 넣기 전 내용 그대로 되돌린다(넣은 글자만큼 밀어서)
            if (caretHere) _box.Select(_at, 0);
            else if (start >= at) _box.Select(start + piece.Length, length);
            else if (start + length > at) _box.Select(start, length + piece.Length);
            else _box.Select(start, length);
        }

        public override void Detach() => _box.TextChanged -= OnTextChanged;

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_inserting) return;

            foreach (var change in e.Changes)
            {
                if (change.Offset > _at) continue;
                _at = change.Offset + change.AddedLength + Math.Max(0, _at - (change.Offset + change.RemovedLength));
            }
        }
    }
}
