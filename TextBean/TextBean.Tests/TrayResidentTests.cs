using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using TextBean.Models;
using TextBean.Services;
using TextBean.ViewModels;
using TextBean.Views;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 트레이 상주 (add-tray-resident, D-092~D-106). 화면 시험은 실제 MainWindow 를 화면 밖에 띄운다 —
/// 활성화 · 최소화 풀기는 바꿔 끼우고(D-082), 실제 트레이 아이콘 · 전원 알림은 만들지 않는다(가짜만, D-103).
/// ✕ 는 실제와 같은 메시지(WM_SYSCOMMAND/SC_CLOSE)를 보낸다 — 운영과 다른 길로 시험하지 않는다 (LL-089).
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class TrayResidentTests
{
    private const string OtherKey = "second-key-5678";

    /// 활성화 · 최소화 풀기를 바꿔 끼우고 횟수를 센다. 화면 밖 시험 창을 앞 창으로 만들지 않는다 (D-082).
    private sealed class Seams
    {
        public int Activations;
        public int Restores;

        public Seams(MainWindow window, bool canHide = true)
        {
            window.ActivateWindow = _ => Activations++;
            window.RestoreWindow = _ => Restores++;
            window.CanHideToTray = canHide;
        }
    }

    private static void PumpUntil(Func<bool> done, string what, int seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            Pump(DispatcherPriority.Background);
        }
    }

    private static void Hide(TabScene scene)
    {
        Wait(scene.Window.RequestHideAsync());
        Assert.False(scene.Window.IsVisible, "숨지 않았다");
    }

    private static Func<bool> TrackClosed(Window window)
    {
        var closed = false;
        window.Closed += (_, _) => closed = true;
        return () => closed;
    }

    private static void ReadOnly(string path, bool on)
        => File.SetAttributes(path, on ? FileAttributes.ReadOnly : FileAttributes.Normal);

    /// 일정 시간 펌프한다 — "그 사이 두 번째 저장 · 알림이 오지 않는다"를 볼 때만 쓴다.
    private static void PumpFor(TimeSpan span)
    {
        var deadline = DateTime.UtcNow + span;
        while (DateTime.UtcNow < deadline) Pump(DispatcherPriority.Background);
    }

    /// <summary>
    /// 탭 하나의 저장을 붙잡는다 — 그 편집기의 저장소만 가짜로 바꿔 낀다. 실제 저장은 캐시 때문에 동기로 끝나기도 해
    /// "저장하는 중"을 타이밍에 맡기면 간헐 실패가 된다 (LL-079). 시험 장면은 저장소를 주입받지 않아 필드를 바꾼다.
    /// 저장이 붙잡힌 순간은 <see cref="Entered"/> 로 안다.
    /// </summary>
    private sealed class SaveGate
    {
        public GatedDocumentStore Store { get; }
        public bool Entered { get; private set; }

        public SaveGate(EditorViewModel tab)
        {
            var field = typeof(EditorViewModel).GetField("_store", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Store = new GatedDocumentStore((TextBean.Services.Interfaces.IDocumentStore)field.GetValue(tab)!);
            Store.DuringSave = () => Entered = true;
            field.SetValue(tab, Store);
        }
    }

    // ── ✕ (SC_CLOSE) ─────────────────────────────────────────────────────────

    [Fact]
    public void 트레이가_있으면_닫기_단추는_창을_숨기고_앱과_키를_남긴다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var closed = TrackClosed(scene.Window);

        // 실제 ✕ 와 같은 메시지다. 화면 밖 창을 활성화하지 않는다 [실측 — 탐침]
        SystemCommands.CloseWindow(scene.Window);
        PumpUntil(() => !scene.Window.IsVisible, "✕ 로 숨지 않았다");
        Pump();

        Assert.False(closed());
        Assert.Single(scene.Shell.Tabs);
        Assert.True(scene.Shell.HasKey);         // 다시 열 때 키를 묻지 않는다 — 이 기능의 목적
    });

    [Fact]
    public void 트레이가_없으면_닫기_단추는_지금처럼_저장하고_끈다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window, canHide: false);
        var closed = TrackClosed(scene.Window);
        scene.Tab(0).Text = "끄기 전 값";

        SystemCommands.CloseWindow(scene.Window);
        PumpUntil(closed, "✕ 로 닫히지 않았다");

        Assert.False(scene.Tab(0).IsDirty);
        Assert.Equal(0, scene.Dialogs.ConfirmCount);       // 첫 안내는 숨기기에만 뜬다
    });

    [Fact]
    public void 코드의_Close_는_트레이가_있어도_진짜로_닫는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);

        Close(scene.Window);        // 닫힐 때까지 펌프한다 — 안 닫히면 던진다

        Assert.Equal(0, scene.Dialogs.ConfirmCount);
    });

    // ── 첫 안내 (D-096) ──────────────────────────────────────────────────────

    [Fact]
    public void 첫_숨기기만_안내하고_취소하면_숨지_않으며_본_것은_설정에_남는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var dialogs = scene.Dialogs;

        dialogs.ConfirmResult = false;
        Wait(scene.Window.RequestHideAsync());
        Assert.True(scene.Window.IsVisible);
        Assert.Equal(["트레이로 숨깁니다"], dialogs.ConfirmTitles);

        dialogs.ConfirmResult = true;
        Hide(scene);
        Assert.Equal(2, dialogs.ConfirmCount);

        scene.Window.ShowFromTray();
        Hide(scene);
        Assert.Equal(2, dialogs.ConfirmCount);                             // 한 번만

        var settings = new AppSettingsService(Path.Combine(scene.Vault.Root, "settings.json"));
        Wait(settings.LoadAsync());
        Assert.True(settings.Current.TrayNoticeShown);                     // 다시 켜도 묻지 않는다
    });

    /// 안내 창은 띄우지 않고 만들기만 해 글을 읽는다 — 꼭 알아야 할 세 가지(키 유지 · 절전 잠금 · 끄는 법) (D-109).
    [Fact]
    public void 첫_안내는_키_유지_절전_잠금_끄는_법을_짧게_말한다() => Run(() =>
    {
        var text = new TextBean.Views.Dialogs.TrayNoticeDialog().Text;

        Assert.Contains("알림 영역으로 숨깁니다", text);
        Assert.Contains("키는 그대로", text);
        Assert.Contains("절전하면 잠깁니다", text);
        Assert.Contains("우클릭 → 종료", text);
        Assert.Contains("Ctrl+Q", text);
    });

    [Fact]
    public void 안내를_봤다는_기록을_못_써도_숨긴다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var file = Path.Combine(scene.Vault.Root, "settings.json");
        File.WriteAllText(file, "{}");
        ReadOnly(file, true);
        try
        {
            Hide(scene);
            Assert.Equal(0, scene.Dialogs.ErrorCount);
        }
        finally { ReadOnly(file, false); }
    });

    // ── 숨기기 전 저장 (D-095) ───────────────────────────────────────────────

    [Fact]
    public void 숨기기_전에_입력한_내용을_저장한다() => Run(() =>
    {
        using var scene = TabScene.Open(2);
        _ = new Seams(scene.Window);
        scene.Tab(0).Text = "배경 탭 값";
        scene.Tab(1).Text = "보던 탭 값";

        Hide(scene);

        Assert.All(scene.Shell.Tabs, tab => Assert.False(tab.IsDirty));
        Assert.Equal("배경 탭 값", Wait(scene.Store.LoadAsync(scene.Paths[0])).Text);
    });

    [Fact]
    public void 저장에_실패하면_숨지_않고_그_탭을_보인다() => Run(() =>
    {
        using var scene = TabScene.Open(2);
        _ = new Seams(scene.Window);
        var failing = scene.Tab(0);
        failing.Text = "저장 실패할 값";
        scene.Shell.ActiveTab = scene.Tab(1);

        ReadOnly(scene.Paths[0], true);
        try
        {
            Wait(scene.Window.RequestHideAsync());

            Assert.True(scene.Window.IsVisible);
            Assert.Same(failing, scene.Shell.ActiveTab);
            Assert.Equal("숨기기 불가", scene.Dialogs.LastErrorTitle);
            Assert.Equal(1, scene.Dialogs.ErrorCount);
        }
        finally { ReadOnly(scene.Paths[0], false); }
    });

    [Fact]
    public void 키_때문에_저장이_막힌_탭은_버리기를_묻지_않고_숨지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        File.WriteAllBytes(scene.Paths[0], TestKeys.Codec(OtherKey).EncryptForNew("다른 키의 문서"));
        scene.Tab(0).Text = "지금 키로 친 값";

        Wait(scene.Window.RequestHideAsync());

        Assert.True(scene.Window.IsVisible);
        Assert.True(scene.Tab(0).SaveBlockedByKey);
        Assert.Equal("숨기기 불가", scene.Dialogs.LastErrorTitle);
        Assert.Equal(["트레이로 숨깁니다"], scene.Dialogs.ConfirmTitles);   // 첫 안내뿐 — 버리기를 묻지 않았다
        Assert.Single(scene.Shell.Tabs);
    });

    [Fact]
    public void 키를_확인하는_중에는_숨기지_않고_알린다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var dialogs = scene.Dialogs;
        var busy = false;
        bool? prepared = null;

        // 키 전환 확인 창이 떠 있는 동안이 "키 확인 중"이다
        dialogs.KeyEntries.Enqueue(new KeyEntry(OtherKey, OtherKey));
        dialogs.ConfirmResult = false;
        dialogs.DuringConfirm = () =>
        {
            dialogs.DuringConfirm = null;
            busy = scene.Shell.IsKeyBusy;
            var hide = scene.Shell.PrepareHideAsync();
            prepared = hide.IsCompleted ? hide.Result : null;
        };

        scene.Shell.ChangeKeyCommand.Execute(null);
        Wait(scene.Shell.Pending);

        Assert.True(busy);
        Assert.False(prepared);
        Assert.Equal("잠시 기다려 주세요", dialogs.LastErrorTitle);
        Assert.DoesNotContain("트레이로 숨깁니다", dialogs.ConfirmTitles);   // 바쁨 검사가 첫 안내보다 먼저다
    });

    /// 운영의 "대화상자 없는 바쁨"(잠그기 저장을 기다리는 중) 에 ✕ 가 오는 길 — 창에서 시작한다 (LL-089).
    [Fact]
    public void 키_작업이_대화상자_없이_도는_중에_닫기_단추를_누르면_숨지_않고_알린다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var gate = new SaveGate(scene.Tab(0));
        scene.Tab(0).Text = "잠그기 전 값";
        gate.Store.Close();

        scene.Shell.LockCommand.Execute(null);
        PumpUntil(() => gate.Entered, "잠그기 저장이 시작되지 않았다");
        Assert.True(scene.Shell.IsKeyBusy);

        Wait(scene.Window.RequestHideAsync());

        Assert.True(scene.Window.IsVisible);
        Assert.Equal("잠시 기다려 주세요", scene.Dialogs.LastErrorTitle);
        Assert.DoesNotContain("트레이로 숨깁니다", scene.Dialogs.ConfirmTitles);

        gate.Store.Open();
        Wait(scene.Shell.Pending);
    });

    [Fact]
    public void 숨길_때_검색_창도_감추고_검색어를_비우고_검색을_끊는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);

        // 검색 창의 Present 는 Activate 라 쓰지 않는다(D-082) — 화면 밖에 띄워 주 창에 잇는다
        var searchVm = new SearchViewModel(scene.Shell);
        var search = new SearchWindow { Owner = scene.Window, DataContext = searchVm };
        ShowOffscreen(search, 400, 300);
        typeof(MainWindow).GetField("_searchWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                          .SetValue(scene.Window, search);
        var searching = new CancellationTokenSource();
        typeof(ShellViewModel).GetField("_searchCts", BindingFlags.Instance | BindingFlags.NonPublic)!
                              .SetValue(scene.Shell, searching);
        searchVm.Query = "검색어도 비밀";

        Hide(scene);

        Assert.False(search.IsVisible);                    // 소유 창은 주인을 따라 숨지 않는다 [실측]
        Assert.Equal("", searchVm.Query);
        Assert.True(searching.IsCancellationRequested);
    });

    // ── 다시 열기 ────────────────────────────────────────────────────────────

    [Fact]
    public void 트레이에서_열면_숨은_창이_보이고_대기_중에는_받지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var seams = new Seams(scene.Window);
        Hide(scene);

        scene.Window.AcceptsRequests = false;              // Windows 종료 · 절전 대기 중
        scene.Window.ShowFromTray();
        Assert.False(scene.Window.IsVisible);
        Assert.Equal(0, seams.Activations);

        scene.Window.AcceptsRequests = true;
        scene.Window.ShowFromTray();
        Assert.True(scene.Window.IsVisible);
        Assert.Equal(1, seams.Activations);
        Assert.Equal(0, seams.Restores);                   // 최소화가 아니면 크기를 건드리지 않는다
    });

    [Fact]
    public void 닫힌_창은_열기_종료_숨기기를_조용히_무시한다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var seams = new Seams(scene.Window);
        Close(scene.Window);

        // 닫힌 창의 Show 는 던진다 — 트레이 · 두 번째 실행이 늦게 와도 터지지 않는다
        scene.Window.ShowFromTray();
        scene.Window.RequestExit();
        Wait(scene.Window.RequestHideAsync());

        Assert.Equal(0, seams.Activations);
    });

    // ── 진짜 종료 (D-097) ────────────────────────────────────────────────────

    [Fact]
    public void 종료는_숨은_창도_보인_뒤_저장하고_끈다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var seams = new Seams(scene.Window);
        var closed = TrackClosed(scene.Window);
        Hide(scene);
        scene.Tab(0).Text = "종료 전 값";

        scene.Window.RequestExit();
        Assert.True(scene.Window.IsVisible);               // 저장 실패 대화상자가 보이는 창 위에 떠야 한다
        Assert.Equal(1, seams.Activations);

        PumpUntil(closed, "종료로 닫히지 않았다");
        Assert.False(scene.Tab(0).IsDirty);
    });

    [Fact]
    public void 종료_명령은_창의_종료_절차로_간다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var closed = TrackClosed(scene.Window);

        scene.Shell.ExitCommand.Execute(null);             // 도구 모음 「종료」 · Ctrl+Q

        PumpUntil(closed, "종료 명령으로 닫히지 않았다");
    });

    [Fact]
    public void 종료_저장에_실패하면_끄지_않고_다음_종료는_다시_받는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var closed = TrackClosed(scene.Window);
        scene.Tab(0).Text = "저장 실패할 값";

        ReadOnly(scene.Paths[0], true);
        try
        {
            scene.Window.RequestExit();
            PumpUntil(() => scene.Dialogs.ErrorCount == 1, "실패를 알리지 않았다");
            Pump();

            Assert.False(closed());
            Assert.True(scene.Window.IsVisible);
            Assert.Equal("종료 불가", scene.Dialogs.LastErrorTitle);
        }
        finally { ReadOnly(scene.Paths[0], false); }

        // 실패가 종료 중 표시를 남기지 않는다 — 남으면 숨기기 · 종료가 영영 막힌다
        scene.Window.RequestExit();
        PumpUntil(closed, "두 번째 종료로 닫히지 않았다");
    });

    [Fact]
    public void 대화상자가_떠_있으면_종료는_창만_보이고_끄지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var seams = new Seams(scene.Window);
        var closed = TrackClosed(scene.Window);
        Hide(scene);

        // 앱의 확인 · 오류 창(MessageBox)은 스레드 모달에 걸리지 않는다 — 대화상자 서비스가 알려 준다 (D-104)
        scene.Dialogs.IsShowing = true;
        scene.Window.RequestExit();
        Pump();

        Assert.True(scene.Window.IsVisible);
        Assert.Equal(1, seams.Activations);
        Assert.False(closed());
        scene.Dialogs.IsShowing = false;
    });

    [Fact]
    public void 키_확인_중에_닫으려다_막혀도_숨기기와_종료는_다시_된다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var dialogs = scene.Dialogs;
        var closed = TrackClosed(scene.Window);

        int? errorsInsideClosing = null;
        dialogs.KeyEntries.Enqueue(new KeyEntry(OtherKey, OtherKey));
        dialogs.ConfirmResult = false;
        dialogs.DuringConfirm = () =>
        {
            dialogs.DuringConfirm = null;
            dialogs.IsShowing = false;                     // 대화상자 검사를 지나 바쁨 검사까지 가게
            scene.Window.Close();                          // 바쁨 → 안내 후 취소
            errorsInsideClosing = dialogs.ErrorCount;      // Close 가 돌아온 순간 = Closing 이 끝난 순간
        };
        scene.Shell.ChangeKeyCommand.Execute(null);
        Wait(scene.Shell.Pending);
        Pump();

        // 안내는 Closing 밖에서 뜬다 — 안에서 띄운 채 Windows 종료가 오면 WPF 종료 처리가 던진다 (R2)
        Assert.Equal(0, errorsInsideClosing);
        Assert.Equal("잠시 기다려 주세요", dialogs.LastErrorTitle);

        dialogs.ConfirmResult = true;
        Hide(scene);
        scene.Window.RequestExit();
        PumpUntil(closed, "바쁨 취소 뒤 종료가 막혔다");
    });

    // ── 겹침 배제 (D-105) ────────────────────────────────────────────────────

    [Fact]
    public void 숨기는_중에_다시_숨기기나_닫기가_와도_한_번만_숨는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var dialogs = scene.Dialogs;
        var closed = TrackClosed(scene.Window);
        Task? again = null;

        // 첫 안내가 떠 있는 동안 = 숨기는 중
        dialogs.DuringConfirm = () =>
        {
            dialogs.DuringConfirm = null;
            dialogs.IsShowing = false;                     // 대화상자 검사가 아니라 숨기는 중 표시로 막히는지 본다
            again = scene.Window.RequestHideAsync();
            scene.Window.Close();
        };

        Hide(scene);
        Pump();

        Assert.True(again!.IsCompleted);
        Assert.Equal(1, dialogs.ConfirmCount);
        Assert.False(closed());
    });

    [Fact]
    public void 종료_저장_중에는_다시_닫기나_숨기기를_받지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var dialogs = scene.Dialogs;
        var closed = TrackClosed(scene.Window);
        Task? hide = null;
        scene.Tab(0).Text = "저장 실패할 값";

        // 종료 저장 실패 알림이 떠 있는 동안 = 종료 중
        dialogs.DuringError = () =>
        {
            dialogs.DuringError = null;
            dialogs.IsShowing = false;
            scene.Window.Close();
            hide = scene.Window.RequestHideAsync();
        };

        ReadOnly(scene.Paths[0], true);
        try
        {
            scene.Window.RequestExit();
            PumpUntil(() => hide is not null, "실패를 알리지 않았다");

            // 두 번째 닫기가 저장을 시작했다면 그 저장도 읽기 전용에 막혀 곧 알린다 — 그만큼 기다려 본다 (R14)
            PumpFor(TimeSpan.FromSeconds(1));

            Assert.True(hide!.IsCompleted);
            Assert.Equal(1, dialogs.ErrorCount);           // 저장 · 알림이 두 번 돌지 않았다
            Assert.Equal(0, dialogs.ConfirmCount);         // 숨기기가 시작되지 않았다(첫 안내 없음)
            Assert.True(scene.Window.IsVisible);
            Assert.False(closed());
        }
        finally { ReadOnly(scene.Paths[0], false); }
    });

    [Fact]
    public void 숨기는_사이_Windows_종료가_오면_숨기지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        scene.Dialogs.DuringConfirm = () =>
        {
            scene.Dialogs.DuringConfirm = null;
            scene.Window.AcceptsRequests = false;          // SessionEndSave 가 세운다
        };

        Wait(scene.Window.RequestHideAsync());

        Assert.True(scene.Window.IsVisible);
        scene.Window.AcceptsRequests = true;
    });

    [Fact]
    public void 숨기기는_트레이_없음_숨은_창_대기_중_대화상자_중에는_시작하지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var seams = new Seams(scene.Window, canHide: false);
        var window = scene.Window;

        Wait(window.RequestHideAsync());
        Assert.True(window.IsVisible);                     // 트레이 없음

        window.CanHideToTray = true;
        window.AcceptsRequests = false;
        Wait(window.RequestHideAsync());
        Assert.True(window.IsVisible);                     // 대기 중
        window.AcceptsRequests = true;

        scene.Dialogs.IsShowing = true;
        Wait(window.RequestHideAsync());
        Assert.True(window.IsVisible);                     // 대화상자 중
        scene.Dialogs.IsShowing = false;
        Assert.Equal(0, scene.Dialogs.ConfirmCount);

        Hide(scene);
        scene.Tab(0).Text = "숨은 채 바뀐 값";
        Wait(window.RequestHideAsync());                   // 이미 숨었다 — 다시 저장 · 안내하지 않는다
        Assert.True(scene.Tab(0).IsDirty);
        Assert.Equal(1, scene.Dialogs.ConfirmCount);
        Assert.Equal(0, seams.Activations);
    });

    [Fact]
    public void 닫기의_저장_대화상자는_Closing_이_끝난_뒤에_뜬다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var closed = TrackClosed(scene.Window);
        var gate = new SaveGate(scene.Tab(0));
        scene.Tab(0).Text = "지금 키로 친 값";

        // 저장이 첫 await 전에 동기로 실패하는 길 — 다른 키 문서라 버리기 확인을 띄운다
        gate.Store.DuringSave = () => throw new KeyUnavailableException(KeyUnavailableReason.FileLockedWithOtherKey);
        scene.Dialogs.ConfirmResult = false;

        scene.Window.Close();
        Assert.Equal(0, scene.Dialogs.ConfirmCount);      // Close 가 돌아온 순간(= Closing 이 끝난 순간)에는 아직 없다 (R2)

        PumpUntil(() => scene.Dialogs.ConfirmCount == 1, "버리기 확인이 뜨지 않았다");
        Pump();
        Assert.False(closed());

        gate.Store.DuringSave = null;
        scene.Dialogs.ConfirmResult = true;
    });

    [Fact]
    public void 닫기를_이어가기_전에_Windows_종료_대기가_시작되면_멈추고_다음_닫기는_받는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var closed = TrackClosed(scene.Window);
        scene.Tab(0).Text = "끄기 전 값";

        scene.Window.RequestExit();
        scene.Window.AcceptsRequests = false;              // Closing 이 끝나고 이어가기 전에 대기가 시작됐다
        Pump();
        Pump();

        Assert.False(closed());
        Assert.True(scene.Tab(0).IsDirty);                 // 저장을 시작하지 않았다 — Windows 종료는 바로 닫기 길이 맡는다

        scene.Window.AcceptsRequests = true;
        Close(scene.Window);                               // 종료 중 표시가 남지 않았다
    });

    [Fact]
    public void 숨기기_저장을_기다리는_사이_대화상자가_뜨면_숨지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var gate = new SaveGate(scene.Tab(0));
        scene.Tab(0).Text = "숨기기 전 값";
        gate.Store.Close();

        var hiding = scene.Window.RequestHideAsync();
        PumpUntil(() => gate.Entered, "숨기기 저장이 시작되지 않았다");

        scene.Dialogs.IsShowing = true;                    // 그 사이 다른 명령(삭제 확인 등)이 대화상자를 띄웠다
        gate.Store.Open();
        Wait(hiding);

        Assert.True(scene.Window.IsVisible);               // 주인이 숨으면 대화상자만 작업 표시줄 단추 없이 남는다 (R3)
        Assert.False(scene.Tab(0).IsDirty);                // 저장은 했다
        scene.Dialogs.IsShowing = false;
    });

    [Fact]
    public void 숨기기_준비_중_예외는_알리고_숨지_않으며_다음_닫기_단추는_받는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        scene.Dialogs.DuringConfirm = () => throw new IOException("시험이 일부러 낸 실패");

        Wait(scene.Window.RequestHideAsync());

        Assert.True(scene.Window.IsVisible);
        Assert.Equal("숨기기 실패", scene.Dialogs.LastErrorTitle);

        scene.Dialogs.DuringConfirm = null;
        Hide(scene);                                       // 숨는 중 표시가 남지 않았다
    });

    /// <summary>
    /// 훅을 직접 부른다 — 최소화 · 이동 · Alt(시스템 메뉴)를 실제로 보내면 화면 밖 시험 창이 움직이거나 활성화된다 (D-082).
    /// 실제 SC_CLOSE 길은 위 시험이 <c>SystemCommands.CloseWindow</c> 로 본다.
    /// </summary>
    [Fact]
    public void 닫기만_숨기기로_바꾸고_다른_시스템_명령은_그대로_둔다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var hook = typeof(MainWindow).GetMethod("OnWindowMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;

        bool Send(int message, int command)
        {
            object[] args = [IntPtr.Zero, message, new IntPtr(command), IntPtr.Zero, false];
            hook.Invoke(scene.Window, args);
            return (bool)args[4];
        }

        const int WmSysCommand = 0x0112;
        Assert.False(Send(WmSysCommand, 0xF020));          // 최소화
        Assert.False(Send(WmSysCommand, 0xF100));          // Alt — 시스템 메뉴
        Assert.False(Send(WmSysCommand, 0xF010));          // 이동
        Assert.False(Send(0x0010, 0xF060));                // WM_CLOSE 는 SC_CLOSE 가 아니다 — 코드의 Close() 와 같은 길
        Pump();
        Assert.True(scene.Window.IsVisible);
        Assert.Equal(0, scene.Dialogs.ConfirmCount);       // 숨기기가 시작되지 않았다

        // 하위 4비트는 시스템이 쓴다 — 붙어 와도 닫기다 [문서]
        Assert.True(Send(WmSysCommand, 0xF060 | 0x0002));
        PumpUntil(() => !scene.Window.IsVisible, "하위 비트가 붙은 닫기로 숨지 않았다");
    });

    [Fact]
    public void 대기_중의_닫기는_시작하지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var closed = TrackClosed(scene.Window);

        scene.Window.AcceptsRequests = false;
        scene.Window.Close();
        Pump();
        Assert.False(closed());

        scene.Window.AcceptsRequests = true;
        Close(scene.Window);
    });

    [Fact]
    public void 세션_끝_표시가_서면_닫기는_묻지도_저장하지도_않고_닫는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        scene.Tab(0).Text = "저장 실패할 값";

        // 검색 창도 닫고 검색을 끊는다 — 남으면 소유 창이 사라진 뒤에도 복호화가 돈다
        var search = new SearchWindow { Owner = scene.Window, DataContext = new SearchViewModel(scene.Shell) };
        ShowOffscreen(search, 400, 300);
        typeof(MainWindow).GetField("_searchWindow", BindingFlags.Instance | BindingFlags.NonPublic)!
                          .SetValue(scene.Window, search);
        var searching = new CancellationTokenSource();
        typeof(ShellViewModel).GetField("_searchCts", BindingFlags.Instance | BindingFlags.NonPublic)!
                              .SetValue(scene.Shell, searching);

        ReadOnly(scene.Paths[0], true);
        try
        {
            scene.Window.ExitImmediately = true;
            Close(scene.Window);
            Assert.Equal((0, 0), (scene.Dialogs.ErrorCount, scene.Dialogs.ConfirmCount));
            Assert.Null(PresentationSource.FromVisual(search));
            Assert.True(searching.IsCancellationRequested);
        }
        finally { ReadOnly(scene.Paths[0], false); }
    });

    // ── 트레이 연결 (TrayController) ─────────────────────────────────────────

    [Fact]
    public void 트레이_메뉴가_창과_셸에_이어진다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var seams = new Seams(scene.Window, canHide: false);
        var tray = new FakeTrayIcon();
        var controller = new TrayController(tray, scene.Window, scene.Shell);

        Assert.True(scene.Window.CanHideToTray);           // 다 이은 뒤에야 ✕ 가 숨기기다 (D-106)
        Assert.Equal(1, tray.ShowCount);
        Assert.Equal(("TextBean - 키 사용 중", (bool?)true), (tray.ToolTip, tray.HasKey));

        Hide(scene);
        tray.RaiseOpen();
        Assert.True(scene.Window.IsVisible);
        Assert.Equal(1, seams.Activations);

        tray.RaiseLock();
        Wait(scene.Shell.Pending);
        Assert.False(scene.Shell.HasKey);
        Assert.Empty(scene.Shell.Tabs);
        Assert.Contains("TextBean - 키 확인 중", tray.ToolTips);
        Assert.Equal(("TextBean - 키 없음", (bool?)false), (tray.ToolTip, tray.HasKey));

        Hide(scene);
        tray.RaiseEnterKey();                              // 키 입력창은 취소로 끝난다(KeyEntries 비어 있음)
        Wait(scene.Shell.Pending);
        Assert.True(scene.Window.IsVisible);               // 키 입력창이 보이는 창 위에 뜨게 먼저 보인다
        Assert.Equal(["키 입력"], scene.Dialogs.KeyPromptTitles);

        controller.Dispose();
        Assert.False(scene.Window.CanHideToTray);
        Assert.Equal(1, tray.DisposeCount);

        var activations = seams.Activations;
        tray.RaiseOpen();                                  // 끊겼다 — 트레이 → 창
        Assert.Equal(activations, seams.Activations);

        // 셸 → 트레이도 끊겼다. 키를 다시 넣어 툴팁이 바뀔 일을 만든다
        var tips = tray.ToolTips.Count;
        scene.Dialogs.KeyEntries.Enqueue(new KeyEntry(TestKeys.DefaultKey, TestKeys.DefaultKey));
        scene.Shell.ChangeKeyCommand.Execute(null);
        Wait(scene.Shell.Pending);
        Assert.True(scene.Shell.HasKey);
        Assert.Equal(tips, tray.ToolTips.Count);
    });

    [Fact]
    public void 대화상자가_떠_있으면_트레이_잠그기와_키_입력은_창만_보인다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var seams = new Seams(scene.Window);
        var tray = new FakeTrayIcon();
        using var controller = new TrayController(tray, scene.Window, scene.Shell);

        scene.Dialogs.IsShowing = true;
        tray.RaiseLock();
        tray.RaiseEnterKey();
        Wait(scene.Shell.Pending);
        scene.Dialogs.IsShowing = false;

        Assert.True(scene.Shell.HasKey);                   // 대화상자 밑에서 탭을 닫지 않았다
        Assert.Single(scene.Shell.Tabs);
        Assert.Empty(scene.Dialogs.KeyPromptTitles);       // 키 입력창이 겹쳐 뜨지 않았다
        Assert.Equal(2, seams.Activations);
    });

    [Fact]
    public void 대기_중에는_트레이_메뉴가_아무것도_하지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        var seams = new Seams(scene.Window);
        var closed = TrackClosed(scene.Window);
        var tray = new FakeTrayIcon();
        using var controller = new TrayController(tray, scene.Window, scene.Shell);
        Hide(scene);

        scene.Window.AcceptsRequests = false;
        tray.RaiseOpen();
        tray.RaiseLock();
        tray.RaiseEnterKey();
        tray.RaiseExit();
        Wait(scene.Shell.Pending);
        Pump();
        scene.Window.AcceptsRequests = true;

        Assert.True(scene.Shell.HasKey);
        Assert.Empty(scene.Dialogs.KeyPromptTitles);
        Assert.False(scene.Window.IsVisible);
        Assert.False(closed());
        Assert.Equal(0, seams.Activations);
    });

    [Fact]
    public void 트레이_종료는_저장하고_끈다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var closed = TrackClosed(scene.Window);
        var tray = new FakeTrayIcon();
        using var controller = new TrayController(tray, scene.Window, scene.Shell);
        Hide(scene);
        scene.Tab(0).Text = "트레이에서 끄기 전 값";

        tray.RaiseExit();
        PumpUntil(closed, "트레이 종료로 닫히지 않았다");

        Assert.False(scene.Tab(0).IsDirty);
    });

    // ── 도구 모음 · 단축키 ───────────────────────────────────────────────────

    [Fact]
    public void 파일_메뉴의_종료와_Ctrl_Q_는_종료_명령에_묶인다() => Run(() =>
    {
        using var scene = TabScene.Open(1);

        // 위 메뉴 (D-158) — 하위 항목은 메뉴를 열지 않아도 항목 목록에 있다
        var file = FindDescendant<Menu>(scene.Window)!.Items.OfType<MenuItem>().First(m => Equals(m.Header, "파일"));
        var exit = file.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "종료"));
        Assert.Same(scene.Shell.ExitCommand, exit.Command);
        Assert.Same(exit, file.Items.OfType<MenuItem>().Last());       // 파일 메뉴 맨 끝
        Assert.Equal("Ctrl+Q", exit.InputGestureText);

        var key = scene.Window.InputBindings.OfType<KeyBinding>().Single(k => k.Key == Key.Q);
        Assert.Equal(ModifierKeys.Control, key.Modifiers);
        Assert.Same(scene.Shell.ExitCommand, key.Command);
    });

    // ── Windows 종료 (D-098) ─────────────────────────────────────────────────

    [Fact]
    public void Windows_종료는_대화상자_없이_저장을_끝내고_다시_열지_않는다() => Run(() =>
    {
        using var scene = TabScene.Open(2);
        var seams = new Seams(scene.Window);

        // 앞 탭은 저장이 막히고 뒤 탭은 입력 중이다. 조용한 저장은 막힌 탭을 지나 뒤 탭까지 저장한다 —
        // 대화상자 있는 저장(종료)은 앞 탭에서 멈춘다. 운영 오버로드가 조용한 쪽에 이어졌는지 본다 (R15)
        Hide(scene);                                       // 숨은 채 — 숨은 앱은 종료를 막지도 묻지도 못한다
        scene.TypeInto(0, "저장 실패할 값");
        scene.TypeInto(1, "끄기 직전 값");
        var confirms = scene.Dialogs.ConfirmCount;

        ReadOnly(scene.Paths[0], true);
        try
        {
            Assert.True(SessionEndSave.Run(scene.Window, scene.Shell, TimeSpan.FromSeconds(4)));
        }
        finally { ReadOnly(scene.Paths[0], false); }

        Assert.False(scene.Tab(1).IsDirty);
        Assert.Equal("끄기 직전 값", Wait(scene.Store.LoadAsync(scene.Paths[1])).Text);
        Assert.True(scene.Tab(0).IsDirty);
        Assert.Equal((confirms, 0), (scene.Dialogs.ConfirmCount, scene.Dialogs.ErrorCount));
        Assert.Equal(0, scene.Dialogs.SuppressedCount);    // 억제할 대화상자를 부르지도 않았다
        Assert.True(scene.Window.ExitImmediately);
        Assert.Equal(1, scene.Dialogs.SuppressCalls);      // 셸을 거친 억제가 대화상자 서비스에 닿았다
        Assert.Equal(0, scene.Dialogs.SuppressDepth);      // 억제는 기다리는 동안만

        scene.Window.ShowFromTray();
        Assert.False(scene.Window.IsVisible);              // 끝나는 앱을 다시 열지 않는다
        Assert.Equal(0, seams.Activations);
    });

    [Fact]
    public void Windows_종료_대기_중에는_요청과_대화상자를_막고_바로_닫기는_기다린_뒤에_선다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var window = scene.Window;
        var closed = TrackClosed(window);
        var save = new TaskCompletionSource();
        (bool Accepts, bool Immediate, int Suppressed, bool Closed)? during = null;

        // 기다리는 사이 들어온 종료(트레이 · Ctrl+Q). 저장을 붙잡아 기다림을 결정적으로 만든다 (LL-079)
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            window.RequestExit();
            during = (window.AcceptsRequests, window.ExitImmediately, scene.Dialogs.SuppressDepth, closed());
            save.SetResult();
        });

        Assert.True(SessionEndSave.Run(window, () => save.Task, scene.Dialogs.Suppress, TimeSpan.FromSeconds(10)));
        Pump();

        Assert.Equal((false, false, 1, false), during);    // 저장 전에 닫히지 않았다
        Assert.False(closed());
        Assert.True(window.ExitImmediately);
    });

    [Fact]
    public void Windows_종료_저장이_제한_시간을_넘겨도_닫기는_선다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var never = new TaskCompletionSource();

        Assert.False(SessionEndSave.Run(scene.Window, () => never.Task, scene.Dialogs.Suppress, TimeSpan.FromMilliseconds(100)));

        Assert.True(scene.Window.ExitImmediately);
        Assert.Equal(0, scene.Dialogs.SuppressDepth);
    });

    // ── 절전 잠그기 (D-100) ──────────────────────────────────────────────────

    [Fact]
    public void 절전에_들어가면_대화상자_없이_잠그고_요청을_다시_받는다() => Run(() =>
    {
        using var scene = TabScene.Open(2);
        _ = new Seams(scene.Window);
        var power = new FakePowerEvents();
        using var suspend = new SuspendLock(power, scene.Window, scene.Shell, TimeSpan.FromSeconds(5));
        Hide(scene);
        scene.TypeInto(1, "절전 전 값");                 // 숨은 채 입력이 저장되지 않은 상태로 절전
        var confirms = scene.Dialogs.ConfirmCount;

        power.RaiseSuspending();

        Assert.False(scene.Shell.HasKey);
        Assert.Empty(scene.Shell.Tabs);
        Assert.Equal("절전 전 값", Wait(TestKeys.Store(scene.Vault.Root).LoadAsync(scene.Paths[1])).Text);   // 셸의 키는 지워졌다
        Assert.Equal((confirms, 0), (scene.Dialogs.ConfirmCount, scene.Dialogs.ErrorCount));
        Assert.True(scene.Window.AcceptsRequests);
        Assert.Equal(1, scene.Dialogs.SuppressCalls);
        Assert.Equal(0, scene.Dialogs.SuppressDepth);
    });

    [Fact]
    public void 대화상자가_떠_있거나_다른_대기_중이면_절전_잠그기를_건너뛴다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var power = new FakePowerEvents();
        using var suspend = new SuspendLock(power, scene.Window, scene.Shell, TimeSpan.FromSeconds(5));

        scene.Dialogs.IsShowing = true;
        power.RaiseSuspending();
        scene.Dialogs.IsShowing = false;
        Assert.True(scene.Shell.HasKey);

        scene.Window.AcceptsRequests = false;              // Windows 종료 대기 중
        power.RaiseSuspending();
        Assert.False(scene.Window.AcceptsRequests);        // 그 대기의 표시를 건드리지 않는다
        scene.Window.AcceptsRequests = true;
        Assert.True(scene.Shell.HasKey);
        Assert.Single(scene.Shell.Tabs);
    });

    [Fact]
    public void 절전_대기_중에는_요청과_대화상자를_막고_끝나면_푼다() => Run(() =>
    {
        using var scene = TabScene.Open(1);
        _ = new Seams(scene.Window);
        var window = scene.Window;
        var power = new FakePowerEvents();
        var locking = new TaskCompletionSource();
        (bool Accepts, int Suppressed)? during = null;
        var calls = 0;
        using var suspend = new SuspendLock(power, window, () => { calls++; return locking.Task; }, scene.Dialogs.Suppress,
                                            TimeSpan.FromSeconds(10));

        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            during = (window.AcceptsRequests, scene.Dialogs.SuppressDepth);
            locking.SetResult();
        });
        power.RaiseSuspending();

        Assert.Equal((false, 1), during);
        Assert.True(window.AcceptsRequests);
        Assert.Equal(0, scene.Dialogs.SuppressDepth);
        Assert.Equal(1, calls);

        // 제한 시간을 넘겨도 요청을 다시 받는다
        var stuckCalls = 0;
        var stuck = new SuspendLock(power, window, () => { stuckCalls++; return new TaskCompletionSource().Task; },
                                    scene.Dialogs.Suppress, TimeSpan.FromMilliseconds(100));
        suspend.Dispose();
        power.RaiseSuspending();
        Assert.Equal((1, 1), (calls, stuckCalls));         // 해제한 쪽은 다시 돌지 않는다
        Assert.True(window.AcceptsRequests);
        stuck.Dispose();

        Assert.Equal(2, power.DisposeCount);
        power.RaiseSuspending();                           // 둘 다 끊겼다
        Assert.Equal((1, 1), (calls, stuckCalls));
    });

    // ── 깨우기 · 기다리기 ────────────────────────────────────────────────────

    [Fact]
    public void 깨우기_신호는_등록_전에_와도_한_번_돌고_해제_뒤에는_무시된다() => Run(() =>
    {
        using var signal = new AutoResetEvent(false);
        var woke = 0;

        signal.Set();                                      // 첫 실행이 기다리기 전에 둘째가 보낸 신호
        var listener = new WakeListener(signal, () => woke++);
        PumpUntil(() => woke == 1, "등록 전 신호를 잃었다");

        signal.Set();
        PumpUntil(() => woke == 2, "두 번째 신호를 받지 못했다");

        // 신호가 UI 스레드로 넘어온 뒤(아직 안 돈 채) 해제되면 버린다 — 닫힌 창을 다시 띄우면 안 된다
        var dispatcher = Dispatcher.CurrentDispatcher;
        var posted = 0;
        void OnPosted(object? sender, DispatcherHookEventArgs e) => Interlocked.Increment(ref posted);
        dispatcher.Hooks.OperationPosted += OnPosted;
        try
        {
            signal.Set();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Volatile.Read(ref posted) == 0 && DateTime.UtcNow < deadline) Thread.Sleep(5);   // 펌프하지 않고 넘어오기만 기다린다
            Assert.True(Volatile.Read(ref posted) > 0, "세 번째 신호가 UI 스레드로 넘어오지 않았다");

            listener.Dispose();
            Pump();
            Assert.Equal(2, woke);

            // 해제 뒤 신호는 넘어오지도 않는다(대기 등록을 풀었다)
            var before = Volatile.Read(ref posted);
            signal.Set();
            Thread.Sleep(200);
            Assert.Equal(before, Volatile.Read(ref posted));
        }
        finally { dispatcher.Hooks.OperationPosted -= OnPosted; }

        Pump();
        Assert.Equal(2, woke);
    });

    [Fact]
    public void 펌프하며_기다리고_제한_시간에_돌아온다() => Run(() =>
    {
        Assert.True(DispatcherWait.Until(Task.CompletedTask, TimeSpan.FromSeconds(1)));

        var source = new TaskCompletionSource();
        var pumped = false;
        Dispatcher.CurrentDispatcher.BeginInvoke(() =>
        {
            pumped = true;
            source.SetResult();
        });
        var finished = Stopwatch.StartNew();
        Assert.True(DispatcherWait.Until(source.Task, TimeSpan.FromSeconds(10)));
        Assert.True(pumped);                               // 기다리는 동안 디스패처 일이 돌았다 — 화면이 얼지 않는다
        Assert.True(finished.Elapsed < TimeSpan.FromSeconds(5), "끝난 뒤에도 제한 시간까지 기다렸다");

        var watch = Stopwatch.StartNew();
        Assert.False(DispatcherWait.Until(new TaskCompletionSource().Task, TimeSpan.FromMilliseconds(200)));
        Assert.InRange(watch.ElapsedMilliseconds, 150, 5000);
    });

    [Fact]
    public void 내리는_중인_디스패처에서는_기다리지_않는다()
    {
        Exception? failure = null;
        bool? result = null;

        // 시험 호스트 밖 전용 스레드 — 디스패처를 직접 내린다
        var thread = new Thread(() =>
        {
            try
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
                result = DispatcherWait.Until(new TaskCompletionSource().Task, TimeSpan.FromSeconds(5));
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Null(failure);
        Assert.False(result);
    }

    // ── 대화상자 서비스 (D-104) ──────────────────────────────────────────────

    [Fact]
    public void 대화상자_서비스는_떠_있는_동안을_알리고_억제_중에는_띄우지_않는다()
    {
        var dialogs = new DialogService();
        var shown = new List<(string Title, bool Showing)>();

        // 실제 MessageBox 는 띄우지 않는다 — 억제가 깨지면 사용자 화면에 창이 뜬다
        dialogs.ShowMessage = (_, title, _, _) =>
        {
            shown.Add((title, dialogs.IsShowing));
            return MessageBoxResult.OK;
        };

        Assert.True(dialogs.Confirm("확인", "본문"));
        dialogs.Error("오류", "본문");
        Assert.Equal([("확인", true), ("오류", true)], shown);
        Assert.False(dialogs.IsShowing);

        using (dialogs.Suppress())
        {
            using (dialogs.Suppress()) Assert.False(dialogs.Confirm("억제 안쪽", "본문"));

            // 안쪽을 풀어도 바깥 억제가 남는다
            Assert.False(dialogs.Confirm("억제 바깥", "본문"));
            dialogs.Error("억제 바깥", "본문");
        }
        Assert.Equal(2, shown.Count);

        Assert.True(dialogs.Confirm("다시", "본문"));      // 풀리면 다시 뜬다
        Assert.Equal(3, shown.Count);

        // 같은 억제를 두 번 풀어도 한 번만 풀린다 — 두 번 빼면 다음 억제가 걸리지 않는다
        var scope = dialogs.Suppress();
        scope.Dispose();
        scope.Dispose();
        using (dialogs.Suppress()) Assert.False(dialogs.Confirm("억제", "본문"));
        Assert.Equal(3, shown.Count);
    }

    // ── 셸 (대화상자 없는 저장 · 잠그기 · 툴팁) ──────────────────────────────

    private static async Task<(ShellViewModel Shell, FakeDialogs Dialogs, DocumentStore Store)> ShellAsync(TempVault vault)
    {
        var (shell, dialogs, store, _, _) = await ShellFixture.BuildWithKeysAsync(vault, TestKeys.Service());
        return (shell, dialogs, store);
    }

    private static async Task<string> DocumentAsync(TempVault vault, DocumentStore store, string name)
    {
        var path = Path.Combine(vault.Root, name);
        if (name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) await File.WriteAllTextAsync(path, "평문");
        else await store.CreateAsync(path);
        return path;
    }

    [Fact]
    public async Task 트레이_툴팁은_키_상태만_말한다()
    {
        using var vault = new TempVault();
        var (shell, _, store) = await ShellAsync(vault);
        var doc = await DocumentAsync(vault, store, "은행 계좌.tbx");
        await shell.RefreshAsync();
        await shell.OpenAsync(doc);

        Assert.Equal("TextBean - 키 사용 중", shell.TrayToolTip);
        Assert.DoesNotContain("은행", shell.TrayToolTip);         // 문서 이름은 사용자 레지스트리에 남는다 (D-102)

        Assert.True(await shell.LockQuietlyAsync());
        Assert.Equal("TextBean - 키 없음", shell.TrayToolTip);
        Assert.False(shell.HasKey);
        shell.Dispose();
    }

    [Fact]
    public async Task 조용한_저장은_대화상자_없이_저장하고_실패는_묻지_않는다()
    {
        using var vault = new TempVault();
        var (shell, dialogs, store) = await ShellAsync(vault);
        var a = await DocumentAsync(vault, store, "A.tbx");
        var b = await DocumentAsync(vault, store, "B.tbx");
        var c = await DocumentAsync(vault, store, "C.tbx");
        await shell.RefreshAsync();
        foreach (var path in new[] { a, b, c }) await shell.OpenAsync(path);
        var (tabA, tabB, tabC) = (shell.Tabs[0], shell.Tabs[1], shell.Tabs[2]);

        tabA.Text = "A 값";
        tabB.Text = "저장 실패할 값";
        await File.WriteAllBytesAsync(c, TestKeys.Codec(OtherKey).EncryptForNew("다른 키의 문서"));
        tabC.Text = "막힐 값";

        ReadOnly(b, true);
        try
        {
            Assert.False(await shell.SaveAllQuietlyAsync());

            Assert.False(tabA.IsDirty);
            Assert.True(tabB.IsDirty);
            Assert.True(tabC.SaveBlockedByKey);
            Assert.Equal((0, 0), (dialogs.ErrorCount, dialogs.ConfirmCount));   // 버리기도 묻지 않았다
            Assert.Equal(3, shell.Tabs.Count);
        }
        finally { ReadOnly(b, false); }

        shell.Dispose();
    }

    /// <summary>
    /// 디스패처 위에서 돈다 — 실제 앱처럼 await 뒤가 UI 스레드로 돌아온다. 동기화 문맥이 없으면 트리 갱신 뒤가
    /// 스레드 풀에서 단언과 동시에 돌아 "돌아온 순간"을 볼 수 없다 [실측 — 첫 판이 경쟁으로 실패].
    /// </summary>
    [Fact]
    public void 절전_잠그기는_저장된_상태면_첫_await_전에_키를_지우고_평문_탭은_남긴다() => Run(() =>
    {
        using var vault = new TempVault();
        var (shell, dialogs, store) = Wait(ShellAsync(vault));
        var plain = Wait(DocumentAsync(vault, store, "메모.txt"));
        var secret = Wait(DocumentAsync(vault, store, "비밀.tbx"));
        Wait(shell.RefreshAsync());
        Wait(shell.OpenAsync(plain));
        Wait(shell.OpenAsync(secret));                    // 금고 문서를 보던 중 — 트리도 그것을 가리킨다

        var searching = new CancellationTokenSource();
        typeof(ShellViewModel).GetField("_searchCts", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(shell, searching);
        var raised = new List<(string? Name, bool HasKey)>();
        shell.PropertyChanged += (_, e) => raised.Add((e.PropertyName, shell.HasKey));

        var locking = shell.LockQuietlyAsync();

        // Windows 가 기다려 주는 시간이 짧다 — 돌아온 순간 이미 키가 없다
        Assert.False(shell.HasKey);
        Assert.All(shell.Tabs, tab => Assert.True(tab.IsPlainText));
        Assert.Contains((nameof(ShellViewModel.HasKey), false), raised);   // 트레이 메뉴가 트리 갱신을 기다리지 않고 바뀐다
        Assert.False(locking.IsCompleted);                // 트리 갱신(파일 읽기)은 아직이다
        Assert.True(shell.IsKeyBusy);                     // 그동안 다른 명령을 받지 않는다
        Assert.True(searching.IsCancellationRequested);   // 도는 검색이 키를 쥐고 있으면 0 으로 지우는 것이 밀린다

        Assert.True(Wait(locking));
        Assert.False(shell.IsKeyBusy);
        Assert.Single(shell.Tabs);
        Assert.Equal(plain, shell.Selected?.FullPath, ignoreCase: true);   // 닫힌 문서를 가리킨 채 남지 않는다 (J-5)
        Assert.Equal((0, 0), (dialogs.ErrorCount, dialogs.ConfirmCount));
        shell.Dispose();
    });

    [Fact]
    public async Task 절전_잠그기는_입력한_내용을_저장한_뒤_키를_지운다()
    {
        using var vault = new TempVault();
        var (shell, _, store) = await ShellAsync(vault);
        var doc = await DocumentAsync(vault, store, "비밀.tbx");
        await shell.RefreshAsync();
        await shell.OpenAsync(doc);
        shell.Tabs[0].Text = "절전 전 값";

        Assert.True(await shell.LockQuietlyAsync());

        Assert.False(shell.HasKey);
        Assert.Equal("절전 전 값", (await TestKeys.Store(vault.Root).LoadAsync(doc)).Text);
        shell.Dispose();
    }

    [Fact]
    public async Task 절전_잠그기는_하나라도_못_저장하면_아무것도_닫지_않고_키를_남긴다()
    {
        using var vault = new TempVault();
        var (shell, dialogs, store) = await ShellAsync(vault);
        var a = await DocumentAsync(vault, store, "A.tbx");
        var b = await DocumentAsync(vault, store, "B.tbx");
        var c = await DocumentAsync(vault, store, "C.tbx");
        await shell.RefreshAsync();
        foreach (var path in new[] { a, b, c }) await shell.OpenAsync(path);

        shell.Tabs[1].Text = "저장 실패할 값";
        await File.WriteAllBytesAsync(c, TestKeys.Codec(OtherKey).EncryptForNew("다른 키의 문서"));
        shell.Tabs[2].Text = "막힐 값";

        ReadOnly(b, true);
        try
        {
            Assert.False(await shell.LockQuietlyAsync());

            Assert.True(shell.HasKey);                     // 물을 수 없는 자리에서 편집 내용을 버리지 않는다 (fail-open)
            Assert.Equal(3, shell.Tabs.Count);             // 저장된 A 도 닫지 않았다 — 어중간한 상태를 만들지 않는다
            Assert.Equal((0, 0), (dialogs.ErrorCount, dialogs.ConfirmCount));
        }
        finally { ReadOnly(b, false); }

        shell.Dispose();
    }

    [Fact]
    public async Task 절전_잠그기는_키_확인_중이나_여러_탭을_닫는_중이면_건너뛴다()
    {
        using var vault = new TempVault();
        var (shell, dialogs, store) = await ShellAsync(vault);
        var a = await DocumentAsync(vault, store, "A.tbx");
        await shell.RefreshAsync();
        await shell.OpenAsync(a);
        Task<bool>? duringBusy = null;
        Task<bool>? duringClosing = null;
        Task<bool>? hideDuringClosing = null;

        dialogs.KeyEntries.Enqueue(new KeyEntry(OtherKey, OtherKey));
        dialogs.ConfirmResult = false;
        dialogs.DuringConfirm = () =>
        {
            dialogs.DuringConfirm = null;
            duringBusy = shell.LockQuietlyAsync();
        };
        shell.ChangeKeyCommand.Execute(null);
        await shell.Pending;

        // 모든 탭 닫기가 실패를 알리는 동안 = 여러 탭을 닫는 중
        shell.Tabs[0].Text = "저장 실패할 값";
        dialogs.DuringError = () =>
        {
            dialogs.DuringError = null;
            duringClosing = shell.LockQuietlyAsync();
            hideDuringClosing = shell.PrepareHideAsync();   // ✕ 도 겹치지 않는다
        };
        var confirms = dialogs.ConfirmCount;
        ReadOnly(a, true);
        try { await shell.CloseAllTabsRequestedAsync(); }
        finally { ReadOnly(a, false); }

        Assert.True(duringBusy is { IsCompleted: true, Result: false });
        Assert.True(duringClosing is { IsCompleted: true, Result: false });
        Assert.True(hideDuringClosing is { IsCompleted: true, Result: false });
        Assert.Equal(confirms, dialogs.ConfirmCount);     // 숨기기 첫 안내도 뜨지 않았다
        Assert.True(shell.HasKey);
        shell.Dispose();
    }

    /// 클립보드 정리는 다른 프로그램이 클립보드를 쥐면 재시도 · 알림 창으로 늘어질 수 있다 — 키를 먼저 지운다 (R1).
    [Fact]
    public async Task 잠그기는_클립보드를_정리하기_전에_키를_지운다()
    {
        using var vault = new TempVault();
        var (shell, _, _, _, clipboard) = await ShellFixture.BuildWithKeysAsync(vault, TestKeys.Service());
        await shell.RefreshAsync();
        bool? keyWhileClearing = null;
        clipboard.DuringClear = () => keyWhileClearing = shell.HasKey;

        Assert.True(await shell.LockQuietlyAsync());

        Assert.False(keyWhileClearing);
        shell.Dispose();
    }

    [Fact]
    public async Task 키가_없으면_절전_잠그기는_할_일이_없다()
    {
        using var vault = new TempVault();
        var (shell, dialogs, _) = await ShellAsync(vault);
        await shell.RefreshAsync();
        Assert.True(await shell.LockQuietlyAsync());

        Assert.True(await shell.LockQuietlyAsync());      // 두 번째는 키가 없다
        Assert.Equal((0, 0), (dialogs.ErrorCount, dialogs.ConfirmCount));
        shell.Dispose();
    }

    [Fact]
    public void 절전_진입만_잠그기_신호다()
    {
        Assert.True(SystemPowerEvents.IsSuspend(new PowerModeChangedEventArgs(PowerModes.Suspend)));
        Assert.False(SystemPowerEvents.IsSuspend(new PowerModeChangedEventArgs(PowerModes.Resume)));
        Assert.False(SystemPowerEvents.IsSuspend(new PowerModeChangedEventArgs(PowerModes.StatusChange)));

        // 처리기가 판정을 거치는지 — 생성자를 건너뛰어 실제 SystemEvents 를 구독하지 않는다 (D-103)
        var power = (SystemPowerEvents)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SystemPowerEvents));
        var raised = 0;
        power.Suspending += (_, _) => raised++;
        var handler = typeof(SystemPowerEvents).GetMethod("OnPowerModeChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        handler.Invoke(power, [this, new PowerModeChangedEventArgs(PowerModes.Resume)]);
        handler.Invoke(power, [this, new PowerModeChangedEventArgs(PowerModes.StatusChange)]);
        Assert.Equal(0, raised);
        handler.Invoke(power, [this, new PowerModeChangedEventArgs(PowerModes.Suspend)]);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void 절전_잠그기는_못_저장한_탭이_있으면_대화상자_없이_건너뛴다() => Run(() =>
    {
        using var scene = TabScene.Open(2);
        _ = new Seams(scene.Window);
        var power = new FakePowerEvents();
        using var suspend = new SuspendLock(power, scene.Window, scene.Shell, TimeSpan.FromSeconds(5));
        scene.Tab(0).Text = "저장 실패할 값";

        ReadOnly(scene.Paths[0], true);
        try { power.RaiseSuspending(); }
        finally { ReadOnly(scene.Paths[0], false); }

        Assert.True(scene.Shell.HasKey);
        Assert.Equal(2, scene.Shell.Tabs.Count);
        Assert.Equal(0, scene.Dialogs.SuppressedCount);    // 조용한 잠그기에 이어졌다 — 대화상자를 부르지도 않았다 (R15)
        Assert.True(scene.Window.AcceptsRequests);
    });
}
