using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using TextBean.Services;
using TextBean.Services.Interfaces;
using TextBean.ViewModels;
using TextBean.Views.Platform;

namespace TextBean;

public partial class App : Application
{
    private Mutex? _instanceLock;
    private EventWaitHandle? _wake;
    private ClipboardService? _clipboard;
    private ShellViewModel? _shell;
    private readonly List<IDisposable> _disposables = [];

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 시험 호스트 안에서는 조립하지 않는다. 메시지 루프를 돌리는 시험 스레드에서 App 을 만들면 WPF 가 이 메서드를 불러
        // 실제 설정을 다시 쓰고 실제 금고 창을 띄운다 [실측 — LL-087]. 단일 실행 잠금보다 앞이어야 잠금도 쥐지 않는다 (D-079).
        if (!ShouldCompose(Assembly.GetEntryAssembly()))
        {
            AppLog.Warn("startup-skipped", null, null);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 두 번째 실행이 숨은 첫 창을 깨우는 신호 (D-099). 단일 실행 잠금보다 먼저 만든다 — 뒤에 만들면 첫 실행이
        // 아직 만들기 전에 둘째가 신호를 보내 잃는다(먼저 만들면 유실 0/10, 뒤면 수신 0/10) [실측].
        _wake = CreateWakeSignal();

        // 두 창이 같은 문서를 각자 저장하면 나중에 누른 쪽이 조용히 이긴다 (D-008)
        _instanceLock = new Mutex(initiallyOwned: true, "TextBean.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            _wake?.Set();
            Shutdown();
            return;
        }

        // 숨은 창도 "열린 창"으로 센다 [실측 — 리서치]. 마지막 창 기준(기본값)이면 숨겨 두고 다시 쓰는 창(검색 창)이 하나라도
        // 남으면 주 창을 닫아도 꺼지지 않는다. 주 창이 닫히면 끝난다 (D-097).
        // 주의: WPF 는 처음 만든 창을 MainWindow 로 잡는다 — MainWindow 보다 먼저 WPF 창을 띄우면 그 창을 닫는 순간 앱이 꺼진다.
        // ResolveRoot 는 MessageBox · 공용 폴더 창만 쓴다.
        ShutdownMode = ShutdownMode.OnMainWindowClose;

        DispatcherUnhandledException += OnDispatcherException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainException;

        await ComposeAsync();
    }

    /// TextBean 이 진입 어셈블리일 때만 조립한다. 단일 파일 게시에서도 진입 어셈블리는 TextBean 이다 [문서].
    public static bool ShouldCompose(Assembly? entry) => entry == typeof(App).Assembly;

    /// <summary>
    /// 같은 사용자 · 같은 로그온 세션에서만 열리는 자동 리셋 이벤트. 뮤텍스(옛 exe 와 이름을 나눔)에는 이 옵션을 걸지 않는다 —
    /// 옵션 없이 만든 같은 이름을 이 옵션으로 열면 WaitHandleCannotBeOpenedException 이 난다 [실측 — 리서치].
    /// 만들지 못하면(다른 사용자가 같은 이름을 쥠 등) 깨우기 없이 간다 — 둘째 실행은 지금처럼 조용히 끝난다.
    /// </summary>
    private static EventWaitHandle? CreateWakeSignal()
    {
        try
        {
            return new EventWaitHandle(false, EventResetMode.AutoReset, "TextBean.Wake",
                                       new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true });
        }
        catch (Exception ex)
        {
            AppLog.Warn("wake-signal", null, ex);
            return null;
        }
    }

    /// <summary>
    /// 조립 루트. DI 컨테이너를 쓰지 않으므로(D-014) 생성 순서와 해제 책임이 여기에 있다.
    /// 새 서비스를 추가하면 여기 등록한다 — 빠뜨려도 빌드는 통과하고 실행 시점에 터진다.
    /// </summary>
    private async Task ComposeAsync()
    {
        var settings = new AppSettingsService(AppSettingsService.DefaultFilePath);
        await settings.LoadAsync();

        // 창 · 키 입력 창을 만들기 전에 — 창은 만들어질 때 테마를 건다 (D-161 · D-163)
        AppTheme.Select(settings.Current.Theme);

        var dialogs = new DialogService();
        _clipboard = new ClipboardService();
        _disposables.Add(_clipboard);

        var root = ResolveRoot(settings, dialogs);
        if (root is null)
        {
            Shutdown();
            return;
        }

        settings.Current.RootPath = root;
        await settings.SaveAsync();

        // 키 서비스는 셸보다 먼저 넣는다 — 해제가 역순이라 탭(셸)이 먼저 닫히고 키가 나중에 지워진다
        var keys = new VaultKeyService(new Pbkdf2KeyDerivation());
        _disposables.Add(keys);
        var codec = new KeyDocumentCodec(keys);

        // TreeService 인스턴스는 하나뿐이다. 금고 폴더를 바꿀 때는 SetRoot로 루트만 갈아끼운다.
        // 새 인스턴스를 만들어 교체하면 DocumentStore가 옛 인스턴스를 붙잡은 채 남아,
        // 트리는 새 금고를 보여주는데 문서는 하나도 안 열리는 상태가 된다.
        var tree = new TreeService(root);
        var store = new DocumentStore(tree, codec);

        // 편집기는 탭마다 한 벌씩 생기고 사라진다. 그래서 여기서 하나 만들어 넘기지 않고
        // 팩터리를 넘긴다 (D-013 — 타이머가 WPF에 묶여 있어 ViewModel 이 직접 못 만든다).
        var editorFactory = new EditorFactory(store, _clipboard, dialogs);

        // 검색은 TreeService 를 통해서만 열거한다 — 예약 영역(.trash/.history)을 거르는 코드가
        // 거기 안에만 있어서, 직접 열거하면 지운 비밀과 편집 전 값이 결과에 뜬다 [실측].
        var search = new SearchService(tree, store);

        var shell = new ShellViewModel(settings, dialogs, store, tree, editorFactory, search, new ExplorerLauncher(),
                                       keys, _clipboard);

        // 편집기를 _disposables 에 직접 넣지 않는다 — Remove 가 없어 닫힌 탭이 목록에 남고
        // 복호화된 평문이 종료까지 산다. 해제는 탭을 쥔 shell 이 맡는다.
        _disposables.Add(shell);
        _shell = shell;

        var window = new MainWindow { DataContext = shell };

        // 음성 입력 (add-voice-input). 셸 뒤에 넣는다 — 역순 해제에서 셸보다 먼저 마이크 · 모델이 닫힌다
        var dictation = new DictationController(new WasapiVoiceRecorder(), new WhisperSpeechToText(), settings, dialogs,
                                                window.FindBodyFor, () => tree.Root);
        shell.UseDictation(dictation);
        _disposables.Add(dictation);
        MainWindow = window;
        window.Show();

        await shell.RefreshAsync();

        // 트리를 먼저 보인 뒤 키를 묻는다. 예외는 셸의 Guarded 안에서 처리된다 —
        // OnStartup 은 async void 라 여기서 샌 예외는 FailSafe 로 앱을 끈다.
        await shell.StartAsync();

        // 시작 키 입력이 끝난 뒤에 단다. 먼저 달면 트리를 읽는 사이 ✕ 로 숨은 창을 주인으로 키 입력창이 혼자 뜬다
        // (비판 검토 R4, D-108). 그 전의 ✕ 는 지금처럼 종료다 — 열린 문서가 없다. 그 사이 온 두 번째 실행의 신호는
        // 이벤트에 남아 있다가 등록하자마자 한 번 돈다.
        // 셸 뒤에 넣으므로 역순 해제에서 셸보다 먼저 풀린다(트레이가 남은 채 셸이 풀리지 않는다).
        AttachTrayResidency(window, shell);
    }

    /// Windows 종료 때 저장을 기다리는 한도. Windows 는 응답을 약 5초 기다린다 [문서] (D-098).
    internal static readonly TimeSpan SessionSaveLimit = TimeSpan.FromSeconds(4);

    /// 절전 진입 때 잠그기를 기다리는 한도. Windows 는 약 2초 기다린다 [문서] (D-100).
    internal static readonly TimeSpan SuspendLockLimit = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// 트레이 상주 (D-092~D-106). 무엇이 실패해도 앱은 뜬다 — 트레이를 못 만들면 CanHideToTray 가 꺼진 채라
    /// ✕ 는 지금처럼 종료다. 숨은 채 다시 열 길이 없어지는 일이 없다 (D-106).
    /// </summary>
    private void AttachTrayResidency(MainWindow window, ShellViewModel shell)
    {
        ITrayIcon? icon = null;
        try
        {
            icon = new TrayIcon();
            _disposables.Add(new TrayController(icon, window, shell));
        }
        catch (Exception ex)
        {
            AppLog.Warn("tray-create", null, ex);
            try { icon?.Dispose(); } catch (Exception inner) { AppLog.Warn("tray-dispose", null, inner); }
        }

        if (_wake is not null) _disposables.Add(new WakeListener(_wake, window.ShowFromTray));

        try
        {
            _disposables.Add(new SuspendLock(new SystemPowerEvents(), window, shell, SuspendLockLimit));
        }
        catch (Exception ex)
        {
            AppLog.Warn("power-events", null, ex);
        }
    }

    /// <summary>
    /// Windows 종료 · 로그아웃. 기본 처리는 곧바로 Shutdown 해 Closing 의 비동기 저장을 버린다 [실측 — 모의 0/2] —
    /// 그 전에 대화상자 없이 저장을 끝까지 기다린다 (D-098). 취소하지 않는다 — 숨은 앱은 종료를 막을 수 없다 [문서].
    /// 셸이 아직 없으면(금고 폴더를 고르는 중) 저장할 것이 없다.
    /// </summary>
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (MainWindow is MainWindow window && _shell is { } shell)
            SessionEndSave.Run(window, shell, SessionSaveLimit);

        base.OnSessionEnding(e);
    }

    private static string? ResolveRoot(IAppSettingsService settings, IDialogService dialogs)
    {
        var saved = settings.Current.RootPath;
        if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved)) return saved;

        if (!string.IsNullOrWhiteSpace(saved))
        {
            // 쓰던 금고가 사라진 경우. 자동 생성하면 빈 트리가 뜨고 사용자는 데이터가
            // 전부 날아갔다고 오해한다 (8.1-3). 제안값도 쓰지 않는다 — 엉뚱한 새 폴더를
            // 기본값으로 들이밀면 실수로 새 금고를 만들어 버린다.
            dialogs.Error("금고 폴더 없음", $"이전에 쓰던 폴더를 찾을 수 없습니다.\n\n{saved}\n\n폴더를 다시 지정해주세요.");
            return dialogs.PickFolder(saved, "금고 폴더 선택");
        }

        // 최초 실행에만 제안값을 쓴다. 고를 수 있게 미리 만들어 둔다.
        var suggestion = settings.SuggestedDefaultRoot;
        Directory.CreateDirectory(suggestion);
        return dialogs.PickFolder(suggestion, "금고 폴더 선택");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 순서가 뒤바뀌면 타이머를 먼저 죽여 정리가 실행되지 않고 평문이 클립보드에 남는다
        // (PROHIBITED-CUSTOM-06). 정리가 먼저, 해제가 나중이다.
        try { _clipboard?.ClearIfOurs(); } catch (Exception ex) { AppLog.Warn("exit-clipboard", null, ex); }

        for (var i = _disposables.Count - 1; i >= 0; i--)
        {
            try { _disposables[i].Dispose(); } catch (Exception ex) { AppLog.Warn("exit-dispose", null, ex); }
        }

        // 깨우기 대기(WakeListener)는 위에서 이미 풀렸다
        _wake?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        FailSafe(e.Exception);
    }

    private void OnDomainException(object sender, UnhandledExceptionEventArgs e)
        => FailSafe(e.ExceptionObject as Exception);

    private void FailSafe(Exception? ex)
    {
        AppLog.Error("fatal", null, ex);

        // 강제 종료되면 Windows 오류 보고가 평문이 담긴 덤프를 남길 수 있다. 정상 경로로 내린다.
        try { _clipboard?.ClearIfOurs(); } catch { }

        MessageBox.Show($"예기치 못한 오류로 종료합니다.\n({ex?.GetType().Name})", "TextBean",
            MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown();
    }
}
