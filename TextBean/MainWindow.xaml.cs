using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TextBean.ViewModels;
using TextBean.Views;
using TextBean.Views.Dialogs;
using TextBean.Views.Platform;

namespace TextBean;

public partial class MainWindow : Window
{
    private bool _closeConfirmed;

    // 숨기기 · 종료 · Windows 종료 · 절전이 서로 겹치지 않게 하는 표시 (D-105). UI 스레드에서만 오간다.
    private bool _hiding;      // 숨기기 저장 중
    private bool _exiting;     // 종료 저장 중 — 바쁨 검사를 넘은 뒤 세우고, 취소로 끝나면 되돌린다
    private bool _closed;

    /// 트레이 아이콘이 실제로 있을 때만 ✕ 가 숨기기다. TrayController 가 켜고 끈다 — 트레이 없이 숨으면 다시 열 길이 없다 (D-106).
    public bool CanHideToTray { get; set; }

    /// Windows 종료 · 절전 대기 동안 false — 숨기기 · 보이기 · 종료 · 닫기를 새로 받지 않는다 (D-101 · D-105).
    public bool AcceptsRequests { get; set; } = true;

    /// Windows 종료 때 SessionEndSave 가 저장을 마친 뒤 세운다. 닫기 절차가 묻지도 취소하지도 않고 닫는다 (D-098).
    public bool ExitImmediately { get; set; }

    /// <summary>
    /// 활성화 · 최소화 풀기는 이 두 지점에만 둔다 — 화면 시험은 바꿔 끼워 화면 밖 시험 창을 앞 창으로 만들지 않는다 (D-082).
    /// </summary>
    public Action<Window> ActivateWindow { get; set; } = window => window.Activate();
    public Action<Window> RestoreWindow { get; set; } = window => window.WindowState = WindowState.Normal;

    private ShellViewModel Vm => (ShellViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();

        // 복사 가로채기는 EditorBehavior.InterceptCopy 가 TextBox 마다 건다.
        // 여기서 이름으로 붙일 수 없다 — x:Name 은 DataTemplate 안에서 필드를 만들지 않는다 (F-2).
        //
        // 실행취소 초기화(D-010)도 없앴다. TextBox 가 탭마다 하나라 한 탭의 스택에
        // 다른 문서가 섞이지 않는다 — 비우는 방어가 아니라 구조로 막는다.
        Closing += OnClosing;
        DataContextChanged += OnDataContextChanged;

        // ▾ 목록은 열 때마다 셸이 만든다 — 같은 제목일 때만 폴더를 붙이는 판단은 ViewModel 의 일이다 (PROHIBITED-UI-03)
        TabStrip.TabListProvider = () => Vm.TabListEntries();
        TabStrip.TabPicked += OnTabPicked;
    }

    /// <summary>
    /// 본문(.txt 는 TextBox, .tbx 는 RichTextBox)에 포커스를 준다. 한 지점에만 둔다 — 화면 시험은 이것을 바꿔 끼워 실제 키보드 포커스를 잡지 않는다.
    /// 실제 포커스는 Win32 SetFocus 로 화면 밖 시험 창을 앞 창으로 만들 수 있다 (D-082).
    /// </summary>
    public Action<TextBoxBase> FocusBody { get; set; } = textBox => textBox.Focus();

    /// ▾ 목록에서 고른 탭으로 가고, 바로 이어 칠 수 있게 그 본문으로 포커스를 옮긴다 (plan §3-6).
    private void OnTabPicked(object? sender, object entry)
    {
        if (entry is not TabListEntry picked) return;

        // 목록은 연 순간의 스냅숏이다. 그 사이 닫힌 탭(키를 바꾸는 동안 그 키에 묶인 탭이 닫힘 등)을 고르면
        // 닫힌 편집기가 활성이 되어 본문이 비고 Ctrl+W 가 듣지 않았다 [실측 — 적대 검토]
        if (!Vm.Tabs.Contains(picked.Tab)) return;

        Vm.ActiveTab = picked.Tab;

        // 본문은 활성 탭이 바뀐 뒤 배치가 끝나야 보인다(Collapsed → Visible). 메뉴 닫힘의 포커스 복원보다도 뒤다.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (FindActiveBodyTextBox() is { } textBox) FocusBody(textBox);
        });
    }

    /// 활성 탭의 본문(TextBox 또는 RichTextBox). 본문은 탭마다 하나씩 TabBodies 가 만든다.
    public TextBoxBase? FindActiveBodyTextBox()
    {
        if (Vm.ActiveTab is not { } active || TabBodies.ItemContainerGenerator.ContainerFromItem(active) is not DependencyObject host)
            return null;

        return FindDescendant<TextBoxBase>(host);
    }

    /// 서식 본문 우클릭 메뉴 — 표 항목은 표 안일 때만, 그림 항목은 그림 하나를 골랐을 때만 보인다 (D-132 · D-142).
    private void OnBodyContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not RichTextBox { ContextMenu: { } menu } box) return;

        RichTable.UpdateMenu(box, menu);
        RichImage.UpdateMenu(box, menu);
    }

    /// 글자색 · 형광펜 · 표 목록에서 하나를 고르면 목록을 닫는다. 서식 · 표는 고른 것의 명령이 넣는다 (D-127 · D-131).
    private void OnFormatPopupPick(object sender, RoutedEventArgs e)
    {
        for (var at = sender as DependencyObject; at is not null; at = LogicalTreeHelper.GetParent(at))
        {
            if (at is not Popup popup) continue;

            popup.IsOpen = false;
            return;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private void OnActiveTabRevealRequested(object? sender, EventArgs e) => TabStrip.RevealSelected();

    private SearchWindow? _searchWindow;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ShellViewModel old)
        {
            old.PropertyChanged -= OnShellChanged;
            old.SearchWindowRequested -= OnSearchWindowRequested;
            old.ActiveTabRevealRequested -= OnActiveTabRevealRequested;
            old.ExitRequested -= OnExitRequested;
            old.ShortcutsChanged -= OnShortcutsChanged;
            old.ShortcutsRequested -= OnShortcutsRequested;
        }

        if (e.NewValue is ShellViewModel fresh)
        {
            fresh.PropertyChanged += OnShellChanged;
            fresh.SearchWindowRequested += OnSearchWindowRequested;

            // 이미 활성인 탭을 다시 가리켜도(트리·검색에서 열기, 저장 실패 탭으로 이동) 선택 변화가 없어
            // 탭 줄의 어떤 훅도 돌지 않는다 — 셸이 신호를 낸다 (D-077)
            fresh.ActiveTabRevealRequested += OnActiveTabRevealRequested;
            fresh.ExitRequested += OnExitRequested;
            fresh.ShortcutsChanged += OnShortcutsChanged;
            fresh.ShortcutsRequested += OnShortcutsRequested;
        }

        ApplyShortcuts();
    }

    private void OnExitRequested(object? sender, EventArgs e) => RequestExit();

    // ── 단축키 (D-110) ───────────────────────────────────────────────────────

    private readonly List<KeyBinding> _shortcutBindings = [];

    private void OnShortcutsChanged(object? sender, EventArgs e) => ApplyShortcuts();

    /// 셸의 키 표로 창 단축키를 다시 만든다. 옛 키는 지운다 — 남으면 바꾼 뒤에도 옛 키가 듣는다.
    private void ApplyShortcuts()
    {
        foreach (var binding in _shortcutBindings) InputBindings.Remove(binding);
        _shortcutBindings.Clear();

        if (DataContext is not ShellViewModel shell) return;

        foreach (var (id, key) in shell.Shortcuts)
        {
            if (key is null || shell.CommandFor(id) is not { } command) continue;
            if (ShortcutsDialog.ToGesture(key) is not { } gesture) continue;   // 검사를 지난 키라 오지 않는다

            var binding = new KeyBinding(command, gesture);
            _shortcutBindings.Add(binding);
            InputBindings.Add(binding);
        }
    }

    private void OnShortcutsRequested(object? sender, EventArgs e)
    {
        var dialog = new ShortcutsDialog { Owner = this, DataContext = new ShortcutsViewModel(Vm) };
        dialog.ShowDialog();
    }

    // ── 트레이 상주 ──────────────────────────────────────────────────────────
    // ✕ · Alt+F4 · 시스템 메뉴 「닫기」는 WM_SYSCOMMAND/SC_CLOSE 로 온다. 코드의 Close() 는 이 길을 타지 않아
    // 진짜 닫기로 남는다 — 그래서 닫기 절차(LL-080)와 화면 시험을 바꾸지 않는다 [실측 — 탐침] (D-092).
    // 진짜 종료는 RequestExit 한 곳으로 모인다(트레이 「종료」 · 도구 모음 「종료」 · Ctrl+Q).

    private const int WmSysCommand = 0x0112;
    private const int ScClose = 0xF060;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(OnWindowMessage);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 하위 4비트는 시스템이 쓴다 — 가리고 비교한다 [문서]
        if (msg == WmSysCommand && (wParam.ToInt64() & 0xFFF0) == ScClose && CanHideToTray)
        {
            handled = true;

            // 메시지 처리 안에서 대화상자(첫 안내 · 저장 실패)를 띄우지 않는다 — 이 메시지가 끝난 뒤 돈다
            Dispatcher.BeginInvoke(HideFromCloseButton);
        }

        return IntPtr.Zero;
    }

    // async void 인 까닭: 훅은 기다릴 수 없다. Task 를 버리면 예외를 아무도 못 본다 — 여기서는 디스패처로 가
    // FailSafe 가 로그를 남긴다. 저장 · 대화상자의 예외는 셸(PrepareHideAsync)이 이미 처리한다.
    private async void HideFromCloseButton() => await RequestHideAsync();

    /// <summary>
    /// 저장한 뒤 창을 숨긴다 — 앱 · 키 · 트레이는 그대로다 (D-092 · D-095).
    /// 저장을 못 하면 숨기지 않는다. 숨은 앱은 Windows 종료를 막지도 묻지도 못해 숨는 순간이 마지막 확인 자리다.
    /// </summary>
    public async Task RequestHideAsync()
    {
        // 닫힌 창은 IsVisible 이 false 라 여기서 걸린다
        if (!CanHideToTray || _hiding || _exiting || !IsVisible || !AcceptsRequests || IsModal()) return;

        _hiding = true;
        try
        {
            if (!await Vm.PrepareHideAsync()) return;

            // 저장을 기다리는 사이 Windows 종료 · 절전이 왔을 수 있다. 종료(Close)는 숨기는 동안 OnClosing 이 받지 않는다.
            // 그 사이 다른 명령이 대화상자를 띄웠거나 키 작업을 시작했으면 숨지 않는다 — 주인이 숨으면 대화상자만
            // 작업 표시줄 단추 없이 남는다 (비판 검토 R3)
            if (!AcceptsRequests || IsModal() || Vm.IsKeyBusy) return;

            // 소유 창은 주인을 숨겨도 남는다 [실측] — 검색 창(검색어 = 비밀값)도 감춘다
            Vm.CancelSearch();
            _searchWindow?.Conceal();
            Hide();
        }
        finally
        {
            _hiding = false;
        }
    }

    /// 트레이 · 두 번째 실행 · 종료가 부른다. 닫힌 뒤나 Windows 종료 · 절전 대기 중에는 받지 않는다.
    public void ShowFromTray()
    {
        if (_closed || !AcceptsRequests) return;

        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) RestoreWindow(this);
        ActivateWindow(this);
    }

    /// <summary>
    /// 진짜 종료. 창을 보인 뒤 닫기 절차(저장 → 다시 닫기)를 탄다 — 저장 실패 대화상자가 보이는 창 위에 떠야 한다.
    /// Application.Shutdown 을 직접 부르지 않는다: Closing 취소가 무시되어 저장이 버려진다 [실측 — 모의] (D-097).
    /// </summary>
    public void RequestExit()
    {
        ShowFromTray();

        // 대화상자가 떠 있으면 닫지 않는다 — 대화상자 밑에서 창이 닫히면 그 뒤 코드가 풀린 셸에서 돈다.
        // 숨기는 중 · 종료 저장 중 · Windows 종료 · 절전 대기 중이면 OnClosing 이 받지 않는다(숨기기 저장이 곧 끝나니 다시 누르면 된다).
        // 닫힌 창의 Close 는 조용히 아무 일도 하지 않는다 [실측 — 변이 검사]
        if (IsModal()) return;

        Close();
    }

    /// 대화상자가 떠 있는가. 앱의 확인 · 오류 창(MessageBox)은 WPF 스레드 모달에 걸리지 않아 셸(대화상자 서비스)도 본다 (D-104).
    public bool IsModal() => ComponentDispatcher.IsThreadModal || Vm.IsDialogShowing;

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    /// <summary>
    /// 금고 검색 창을 띄운다. 창 생성·표시는 View 의 일이다 (D-013).
    /// 한 번 만들어 재사용한다 — 닫을 때마다 파괴하면 다시 열 때 검색어를 또 쳐야 하고,
    /// 그 검색어가 비밀값이다.
    /// </summary>
    private void OnSearchWindowRequested(object? sender, string? scopeFolder)
    {
        _searchWindow ??= new SearchWindow
        {
            Owner = this,
            DataContext = new SearchViewModel(Vm),
        };

        // 트리에서 "이 폴더에서 찾기" 로 왔으면 대상을 확정한 채 연다
        if (scopeFolder is not null && _searchWindow.DataContext is SearchViewModel vm)
            vm.UseFolder(scopeFolder);

        _searchWindow.Present();
    }

    /// <summary>
    /// 찾기 띠가 열리면 커서를 그리로 보낸다. 안 보내면 Ctrl+F 를 누르고도
    /// 상자를 한 번 더 클릭해야 한다. 포커스 이동은 View 의 일이다.
    /// </summary>
    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.IsFindBarOpen)) return;
        if (!Vm.IsFindBarOpen) return;

        // 방금 보이게 된 요소는 아직 포커스를 받을 수 없다 — 레이아웃 한 번 뒤에 잡는다
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        });
    }

    /// <summary>
    /// 종료 경로의 방어선. 열린 탭 전부를 순차 저장하고, 하나라도 실패하면 종료를 취소한다 (J-3).
    /// Closing은 await 할 수 없어 일단 취소하고, 저장이 끝난 뒤 다시 닫는다.
    /// </summary>
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed) return;

        // Windows 종료: SessionEndSave 가 이미 대화상자 없이 저장했다. 여기서 묻거나 취소하면 Windows 가 앱을 끊는다 (D-098)
        if (ExitImmediately)
        {
            CloseSearchWindow();
            return;
        }

        e.Cancel = true;

        // 종료 저장 중 다시 닫기(Ctrl+Q 연타) · 숨기는 중 · Windows 종료 · 절전 대기 중에는 새 닫기를 시작하지 않는다 —
        // 같은 탭을 두 길이 저장하고 실패 대화상자가 두 번 뜬다 (D-105)
        if (_exiting || _hiding || !AcceptsRequests) return;

        // 대화상자는 Closing 이벤트 밖에서 띄운다. 안에서 띄운 채 Windows 종료가 오면 WPF 의 종료 처리가 닫는 중인 창을
        // 다시 닫으려다 던져 정리(OnExit)를 건너뛴다 — LL-080 과 같은 검사다 (비판 검토 R2, D-108)

        // 키 전환 도중에 닫으면 전환 저장과 종료 저장이 같은 탭을 동시에 저장한다
        if (Vm.IsKeyBusy)
        {
            _ = Dispatcher.BeginInvoke(Vm.WarnBusy);
            return;
        }

        _exiting = true;
        try
        {
            // 저장 중 대화상자(버리기 확인 · 실패 알림)는 첫 await 전에 동기로 뜰 수도 있다 — Closing 을 먼저 끝낸다
            await Dispatcher.Yield(DispatcherPriority.Background);

            // 그 사이 Windows 종료 · 절전 대기가 시작됐으면 여기서 멈춘다(Windows 종료는 바로 닫기 길로 끝난다)
            if (!AcceptsRequests) return;

            // 실패하면 그 탭으로 이동한 채 종료가 취소되고 편집 내용이 남는다 (D-011)
            if (!await Vm.SaveAllForExitAsync()) return;

            CloseSearchWindow();
            _closeConfirmed = true;

            // Closing 이벤트 처리 도중에 Close()를 부르면 InvalidOperationException 이 난다 [실측] (LL-080).
            // 위의 Yield 로 지금은 Closing 밖이지만, 닫기는 그대로 디스패처에 넘겨 현재 일이 끝난 다음 한 번만 실행한다 —
            // Yield 를 누가 지워도 LL-080 이 되살아나지 않게.
            await Dispatcher.InvokeAsync(Close, DispatcherPriority.Background);
        }
        finally
        {
            // 취소로 끝나면(저장 실패 · 예외) 되돌린다 — 안 되돌리면 다시 닫을 수도, 숨긴 뒤 트레이에서 열 수도 없다
            if (!_closeConfirmed) _exiting = false;
        }
    }

    /// 도는 검색을 끊고 검색 창을 실제로 닫는다. 안 닫으면 소유 창이 사라진 뒤에도
    /// 복호화가 계속 돌고 프로세스가 안 끝난다.
    private void CloseSearchWindow()
    {
        Vm.CancelSearch();
        _searchWindow?.ForceClose();
        _searchWindow = null;
    }

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        => Vm.Selected = e.NewValue as TreeNodeViewModel;

    /// <summary>
    /// TreeView 는 우클릭으로 항목을 선택하지 않는다. 그대로 두면 메뉴가 직전에 선택돼 있던
    /// 엉뚱한 항목에 적용된다 — "폴더2에 우클릭해 새 폴더"를 눌렀는데 최상위에 생기는 식이다.
    /// 삭제·이름변경도 같은 메뉴에 있어 조용히 다른 항목을 건드릴 수 있다.
    /// </summary>
    private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = FindTreeViewItem(e.OriginalSource as DependencyObject);
        if (item is not null)
        {
            item.IsSelected = true;
            item.Focus();
            return;
        }

        // 빈 곳을 우클릭했으면 선택을 비운다. 그래야 "빈 곳 = 최상위"가 예측 가능해진다 —
        // 직전 선택이 남아 있으면 어디에 만들어질지 화면만 봐서는 알 수 없다.
        Vm.Selected = null;
    }

    // ── 드래그 이동 ──────────────────────────────────────────────────────────
    // 앱 전용 포맷만 쓴다. DataFormats.FileDrop 을 담으면 탐색기·메일 첨부창으로 끌어 놓는
    // 순간 .tbx 가 금고 밖으로 나가 .trash/.history 보장 밖에 놓인다.
    private const string DragFormat = "TextBean.TreeNode";

    private Point _dragOrigin;
    private TreeNodeViewModel? _dragCandidate;

    private void OnTreeDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragOrigin = e.GetPosition(null);

        // Vm.Selected 를 쓰면 안 된다 — 저장 실패 시 선택이 되돌려져 엉뚱한 항목이 실린다.
        var node = FindTreeViewItem(e.OriginalSource as DependencyObject)?.DataContext as TreeNodeViewModel;
        _dragCandidate = node is { IsRoot: false } ? node : null;
    }

    private void OnTreeDragMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;

        var moved = e.GetPosition(null) - _dragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var payload = _dragCandidate;
        _dragCandidate = null;
        DragDrop.DoDragDrop((DependencyObject)sender,
            new DataObject(DragFormat, payload.FullPath), DragDropEffects.Move);
    }

    /// 문서 위에 놓으면 그 문서가 든 폴더로 해석한다 — TargetFolder() 관례와 같다.
    private static string? DropTargetFolder(DragEventArgs e)
    {
        if (FindTreeViewItem(e.OriginalSource as DependencyObject)?.DataContext is not TreeNodeViewModel node)
            return null;

        // System.Windows.Shapes.Path 와 이름이 겹쳐 WPF 프로젝트에서는 정규화해 쓴다
        return node.IsFolder ? node.FullPath : System.IO.Path.GetDirectoryName(node.FullPath);
    }

    private void OnTreeDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;

        if (!e.Data.GetDataPresent(DragFormat)) return;
        if (e.Data.GetData(DragFormat) is not string source) return;

        if (DropTargetFolder(e) is not { } target)
        {
            Vm.ClearDropHint();      // 트리 빈 곳 — 놓을 대상이 없다
            return;
        }

        // 판정은 ViewModel/Service 가 한다. 여기서는 커서만 정한다 (PROHIBITED-UI-03)
        if (Vm.EvaluateDrop(source, target)) e.Effects = DragDropEffects.Move;
    }

    private void OnTreeDragLeave(object sender, DragEventArgs e) => Vm.ClearDropHint();

    private void OnTreeDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        Vm.ClearDropHint();

        if (!e.Data.GetDataPresent(DragFormat)) return;
        if (e.Data.GetData(DragFormat) is not string source) return;
        if (DropTargetFolder(e) is not { } target) return;

        // 이동은 ViewModel 의 Guarded 안에서 돈다 — 코드비하인드에 async void 를 두지 않는다
        _ = Vm.DropAsync(source, target);
    }

    /// ItemsControl.ContainerFromElement 는 중첩 항목에서 기대대로 동작하지 않는 경우가 있어
    /// 시각 트리를 직접 거슬러 올라간다.
    private static TreeViewItem? FindTreeViewItem(DependencyObject? source) => FindAncestor<TreeViewItem>(source);

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return source as T;
    }

    // ── 탭 순서 바꾸기 ───────────────────────────────────────────────────────
    // 포맷(TabStrip.DragFormat)은 트리와 다르다. 트리의 DragOver/Drop 은 포맷 존재와 문자열 페이로드만 보고
    // 파일 이동을 판정하므로, 같은 포맷이면 탭을 트리 위에 떨구는 순간 금고에서 문서가 실제로 옮겨진다.
    // 놓을 자리 판정 · 끄는 중 자동 넘김 · 삽입선은 TabStrip 이 맡는다 — 탭 줄이 넘기면 탭이 움직이기 때문이다.

    private Point _tabDragOrigin;
    private EditorViewModel? _tabDragCandidate;

    private void OnTabDragStart(object sender, MouseButtonEventArgs e)
    {
        _tabDragOrigin = e.GetPosition(null);
        _tabDragCandidate = null;

        // 닫기 ✕ 버튼이 탭 폭의 63~88% 를 차지하고 드래그 임계는 4px 다 [실측].
        // 이 가드가 없으면 닫으려다 손이 떨리는 것만으로 탭이 끌려간다. ◀ ▶ ▾ 도 버튼이라 같이 걸러진다.
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;

        _tabDragCandidate = FindAncestor<TabItem>(e.OriginalSource as DependencyObject)
                            ?.DataContext as EditorViewModel;
    }

    private void OnTabDragMove(object sender, MouseEventArgs e)
    {
        if (_tabDragCandidate is null || e.LeftButton != MouseButtonState.Pressed) return;

        var moved = e.GetPosition(null) - _tabDragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var payload = _tabDragCandidate;
        _tabDragCandidate = null;

        // 페이로드에는 식별용 인덱스만 싣는다. 경로나 텍스트를 담으면 문서 이름과 금고 내 경로가
        // OLE 클립보드에 올라가 다른 앱이 읽을 수 있다. 드롭 시점에 IndexOf 로 다시 찾는다.
        try
        {
            DragDrop.DoDragDrop((DependencyObject)sender,
                new DataObject(TabStrip.DragFormat, Vm.Tabs.IndexOf(payload)), DragDropEffects.Move);
        }
        finally
        {
            // 놓기 · 취소 · 줄 밖에서 놓기 어느 경로로 끝나도 자동 넘김 타이머를 멈춘다
            TabStrip.DragEnded();
        }
    }

    private void OnTabDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        e.Handled = true;

        var point = e.GetPosition(TabStrip);

        // 트리에서 출발한 드래그도 탭 줄로 들어온다. 포맷을 보고 넘기거나 멈추는 것은 TabStrip 이 한다.
        TabStrip.DragHover(point, e.Data);
        if (!e.Data.GetDataPresent(TabStrip.DragFormat)) { TabStrip.HideInsertion(); return; }

        if (TabStrip.InsertionTargetAt(point) is not { } target) { TabStrip.HideInsertion(); return; }

        e.Effects = DragDropEffects.Move;
        TabStrip.ShowInsertion(target.Item, target.After);
    }

    // 줄 안의 다른 자식으로 넘어가면 그 OLE 호출은 DragLeave · DragEnter 만 올리고 DragOver 는 없다 [실측 — 적대 검토].
    // DragEnter 도 DragOver 처럼 받아야 나감 판정이 취소되고 삽입선이 바로 따라온다.
    private void OnTabDragEnter(object sender, DragEventArgs e) => OnTabDragOver(sender, e);

    // 줄 안 자식 사이를 지날 때도 올라온다 — 줄을 떠났는지는 TabStrip 이 미뤄 판정한다(좌표는 믿을 수 없다)
    private void OnTabDragLeave(object sender, DragEventArgs e) => TabStrip.DragLeft();

    private void OnTabDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        TabStrip.DragEnded();

        if (!e.Data.GetDataPresent(TabStrip.DragFormat)) return;
        if (e.Data.GetData(TabStrip.DragFormat) is not int sourceIndex) return;
        if (sourceIndex < 0 || sourceIndex >= Vm.Tabs.Count) return;
        if (TabStrip.InsertionTargetAt(e.GetPosition(TabStrip)) is not { } target) return;
        if (target.Item.DataContext is not EditorViewModel dropOn) return;

        // 인덱스 계산과 컬렉션 변경은 ViewModel 이 한다. 여기서는 좌표만 푼다 (PROHIBITED-UI-03).
        // 드롭은 OLE 모달 루프 안이라 저장·닫기·대화상자는 두지 않는다.
        Vm.MoveTabTo(Vm.Tabs[sourceIndex], dropOn, target.After);
    }
}
