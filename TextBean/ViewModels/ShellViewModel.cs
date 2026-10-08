using System.Collections.ObjectModel;
using System.IO;
using TextBean.Models;
using TextBean.Services;
using TextBean.Services.Interfaces;

namespace TextBean.ViewModels;

public sealed class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IAppSettingsService _settings;
    private readonly IDialogService _dialogs;
    private readonly IDocumentStore _store;
    private readonly ITreeService _tree;          // 인스턴스는 고정. 루트만 SetRoot로 바꾼다
    private readonly IEditorFactory _editorFactory;
    private readonly ISearchService _search;
    private readonly IExplorerLauncher _explorer;
    private readonly IVaultKeyService _keys;
    private readonly IClipboardService _clipboard;
    private TreeNodeViewModel? _selected;
    private bool _closingMany;
    private CancellationTokenSource? _searchCts;

    public ShellViewModel(IAppSettingsService settings, IDialogService dialogs, IDocumentStore store,
                          ITreeService tree, IEditorFactory editorFactory, ISearchService search,
                          IExplorerLauncher explorer, IVaultKeyService keys, IClipboardService clipboard)
    {
        _settings = settings;
        _dialogs = dialogs;
        _store = store;
        _tree = tree;
        _editorFactory = editorFactory;
        _search = search;
        _explorer = explorer;
        _keys = keys;
        _clipboard = clipboard;

        // RelayCommand 는 CommandManager.RequerySuggested 를 구독하지 않는다.
        // 여기서 알리지 않으면 탭이 하나뿐인데 "다른 탭 닫기" 가 계속 활성으로 보인다.
        Tabs.CollectionChanged += (_, _) =>
        {
            Raise(nameof(HasNoTabs));
            RaiseTabCommandStates();
        };

        // 명령 람다는 Action<object?>로 변환되므로 async 람다는 async void다.
        // 예외가 새면 Dispatcher로 다시 던져져 FailSafe → 앱 종료로 직행한다.
        // 그래서 모든 비동기 명령을 Guarded로 감싼다.
        NewFolderCommand   = new RelayCommand(_ => NewFolder());
        NewDocumentCommand = new RelayCommand(_ => Guarded("문서 만들기", NewDocumentAsync));
        RenameCommand      = new RelayCommand(_ => Guarded("이름 변경", RenameAsync));
        DeleteCommand      = new RelayCommand(_ => Guarded("삭제", DeleteAsync));
        EmptyTrashCommand  = new RelayCommand(_ => EmptyTrash());
        RefreshCommand     = new RelayCommand(_ => Guarded("새로고침", RefreshAsync));
        SaveCommand        = new RelayCommand(_ => Guarded("저장", async () =>
                             {
                                 if (ActiveTab is { } tab) await tab.TrySaveAsync();
                             }));
        CopyAllCommand     = new RelayCommand(_ => ActiveTab?.Copy(null));
        ChangeRootCommand  = new RelayCommand(_ => Guarded("금고 폴더 변경", ChangeRootAsync));
        OpenBackupCommand  = new RelayCommand(_ => Guarded("이전 세대 열기", OpenBackupAsync));
        CloseTabCommand    = new RelayCommand(p => Guarded("탭 닫기", async () =>
                             {
                                 if (p is EditorViewModel tab) await CloseTabAsync(tab);
                             }));

        // 대상은 우클릭한 탭이다. 메뉴가 CommandParameter 로 넘긴다 (C-01) —
        // 활성 탭을 기준으로 잡으면 배경 탭을 우클릭했을 때 엉뚱한 집합이 닫힌다.
        CloseOtherTabsCommand = new RelayCommand(
            p => Guarded("다른 탭 닫기", async () =>
            {
                if (p is EditorViewModel tab) await CloseOtherTabsAsync(tab);
            }),
            _ => Tabs.Count > 1);

        CloseAllTabsCommand = new RelayCommand(
            _ => Guarded("모든 탭 닫기", async () => { await CloseAllTabsRequestedAsync(); }),
            _ => Tabs.Count > 0);

        // Ctrl+W. 단축키에는 넘길 대상이 없으므로 이것만 활성 탭을 기준으로 한다.
        CloseActiveTabCommand = new RelayCommand(
            _ => Guarded("탭 닫기", async () => { if (ActiveTab is { } tab) await CloseTabAsync(tab); }),
            _ => ActiveTab is not null);

        OpenFindBarCommand  = new RelayCommand(_ => OpenFindBar(), _ => ActiveTab is not null);
        OpenSearchWindowCommand = new RelayCommand(_ => SearchWindowRequested?.Invoke(this, null));

        // 트리에서 대상을 확정한 채 검색 창을 연다 — 창이 뜬 뒤 폴더를 다시 고를 필요가 없다
        SearchInFolderCommand = new RelayCommand(
            _ => SearchWindowRequested?.Invoke(this, ScopeFolderFor(SearchScope.Folder)),
            _ => Selected is not null);

        // CanExecute 를 두지 않는다. 트리 메뉴에서는 한 번 비활성으로 열리면 조건이 바뀌어도
        // 다시 열 때 재평가되지 않아 계속 비활성으로 굳는다 [실측 프로브]. 선택이 없으면 루트를 연다.
        OpenInExplorerCommand = new RelayCommand(_ => Guarded("탐색기에서 열기", OpenInExplorerAsync));

        CloseFindBarCommand = new RelayCommand(_ => CloseFindBar());
        FindNextCommand     = new RelayCommand(_ => ActiveTab?.MoveToNextMatch());
        FindPrevCommand     = new RelayCommand(_ => ActiveTab?.MoveToPrevMatch());

        ChangeKeyCommand = new RelayCommand(_ => Guarded("키 변경", ChangeKeyAsync));
        LockCommand      = new RelayCommand(_ => Guarded("잠그기", LockAsync));

        // CanExecute 를 두지 않는다 — 도구 막대에 흐린 단추를 두지 않고(어두운 테마 결정), 메뉴의 꺼짐이 굳는 일도 없다(탐색기에서 열기와 같은 이유).
        // 음성 입력이 붙지 않았거나(D-187) 시작할 수 없는 탭이면 눌러도 아무것도 하지 않는다
        ToggleDictationCommand = new RelayCommand(_ => Guarded("음성 입력", ToggleDictationAsync));

        // 끄는 것은 창의 닫기 절차(저장 → 다시 닫기)다. 바쁨 검사도 거기서 한다 — 여기서 Shutdown 을 부르면
        // Closing 취소가 무시되어 저장이 버려진다 [실측 — 모의] (D-097)
        ExitCommand      = new RelayCommand(_ => ExitRequested?.Invoke(this, EventArgs.Empty));

        // 목록 창을 띄우는 것은 View 의 일이다 (D-013) — 요청만 낸다
        OpenShortcutsCommand = new RelayCommand(_ => ShortcutsRequested?.Invoke(this, EventArgs.Empty));

        // 잘못 적힌 줄은 그 줄만 기본 키로 — 앱은 뜬다 (D-110)
        Shortcuts = ShortcutCatalog.Resolve(_settings.Current.Shortcuts,
                                            id => AppLog.Warn($"shortcut-invalid-{id}", null, null));

        // 모르는 글꼴 id 는 기본 글꼴 (D-155)
        BodyFont = FontChoices.Find(_settings.Current.BodyFont);
        SetBodyFontCommand = new RelayCommand(id => _ = SetBodyFontAsync(id as string));

        // 모르는 테마 id 는 밝게 (D-161)
        Theme = ThemeChoices.Find(_settings.Current.Theme);
        SetThemeCommand = new RelayCommand(id => _ = SetThemeAsync(id as string));
    }

    // ── 테마 (D-161) ─────────────────────────────────────────────────────────
    // 실제 색 · 열린 본문 거두기 · 다시 열기는 View(MainWindow · AppTheme)의 일이다 — 여기는 id 만 든다.

    public ThemeChoice Theme { get; private set; }

    public RelayCommand SetThemeCommand { get; }

    /// 보기 ▸ 테마 목록 — 지금 테마에 ✓.
    public IReadOnlyList<ThemeMenuItem> ThemeMenu => [.. ThemeChoices.All.Select(c => new ThemeMenuItem(c.Id, c.Label, c.Id == Theme.Id))];

    /// 바로 바꾸고 저장한다. 저장에 실패하면 알리고 지금만 쓴다 — 글꼴과 같은 길.
    public async Task SetThemeAsync(string? id)
    {
        var choice = ThemeChoices.Find(id);
        if (choice.Id == Theme.Id) return;

        Theme = choice;
        Raise(nameof(Theme));
        Raise(nameof(ThemeMenu));
        _settings.Current.Theme = choice.Id;

        try { await _settings.SaveAsync(); }
        catch (Exception ex)
        {
            AppLog.Error("theme-save", null, ex);
            _dialogs.Error("테마 저장 실패", "고른 테마는 지금만 쓰이고, 다시 켜면 예전 테마로 돌아갑니다.");
        }
    }

    /// <summary>
    /// 테마를 바꾸기 직전 · 직후 (D-165). 바꾸면 창 안 본문이 새로 만들어진다 [실측] —
    /// 열린 서식 본문을 모두 거둬 두고(<see cref="EditorViewModel.StashBody"/>), 바꾼 뒤 다시 연다(<see cref="EditorViewModel.ReloadBody"/>).
    /// </summary>
    public void StashBodies()
    {
        foreach (var tab in Tabs) tab.StashBody();
    }

    public void ReloadBodies()
    {
        foreach (var tab in Tabs) tab.ReloadBody();
    }

    // ── 본문 글꼴 (D-155 ~ D-157) ────────────────────────────────────────────
    // 고른 글꼴을 실제 글꼴로 바꾸는 것은 View(AppFonts)의 일이다 — 여기는 이름만 든다.

    public FontChoice BodyFont { get; private set; }

    public RelayCommand SetBodyFontCommand { get; }

    /// 도구 모음 「글꼴 ▾」 목록 — 지금 글꼴에 ✓.
    public IReadOnlyList<FontMenuItem> FontMenu => [.. FontChoices.All.Select(c => new FontMenuItem(c.Id, c.Label, c.Id == BodyFont.Id))];

    /// 바로 바꾸고 저장한다. 저장에 실패하면 알리고 지금만 쓴다 — 단축키와 같은 길 (D-110).
    public async Task SetBodyFontAsync(string? id)
    {
        var choice = FontChoices.Find(id);
        if (choice.Id == BodyFont.Id) return;

        BodyFont = choice;
        Raise(nameof(BodyFont));
        Raise(nameof(FontMenu));
        _settings.Current.BodyFont = choice.Id;

        try { await _settings.SaveAsync(); }
        catch (Exception ex)
        {
            AppLog.Error("font-save", null, ex);
            _dialogs.Error("글꼴 저장 실패", "고른 글꼴은 지금만 쓰이고, 다시 켜면 예전 글꼴로 돌아갑니다.");
        }
    }

    /// 활성 상태를 IsEnabled 바인딩과 코드 양쪽에서 정하지 않는다 — 판정은 CanExecute 한 곳이다 (LL-033).
    private void RaiseTabCommandStates()
    {
        CloseTabCommand.RaiseCanExecuteChanged();
        CloseOtherTabsCommand.RaiseCanExecuteChanged();
        CloseAllTabsCommand.RaiseCanExecuteChanged();
        CloseActiveTabCommand.RaiseCanExecuteChanged();
        OpenFindBarCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 열려 있는 문서들. 탭마다 편집기가 한 벌씩이라 실행취소 스택이 문서에 속한다 (C-01).
    /// 탭 줄(TabStrip)과 본문 ItemsControl 만 바인딩한다 — 컬렉션을 다른 컨트롤과 나눠 쓰면 필터가 양쪽에 걸린다 (LL-007).
    /// ▾ 열린 탭 목록은 <see cref="TabListEntries"/> 스냅숏이라 이 규칙 안이다.
    /// </summary>
    public ObservableCollection<EditorViewModel> Tabs { get; } = [];

    private EditorViewModel? _activeTab;

    public EditorViewModel? ActiveTab
    {
        get => _activeTab;
        set
        {
            var previous = _activeTab;
            if (Set(ref _activeTab, value))
            {
                // 본문은 탭마다 한 벌씩 만들어 두고 활성 탭만 보인다. 이 표시가 안 바뀌면 화면이 빈다.
                if (previous is not null) previous.IsActive = false;
                if (value is not null) value.IsActive = true;

                Raise(nameof(Editor));
                RaiseTabCommandStates();
            }

            // 값이 같아도 낸다 — 이미 활성인 탭이 탭 줄에서 밀려나 있으면 선택 변화가 없어 보이게 넘길 길이 없다 (D-077)
            ActiveTabRevealRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// 활성 탭을 탭 줄에서 보이게 하라. ActiveTab 에 값을 넣을 때마다(같은 값이어도) 낸다. 활성 탭은 null 일 수 있다.
    public event EventHandler? ActiveTabRevealRequested;

    /// <summary>
    /// ▾ 열린 탭 목록(탭 줄 순서). 같은 제목이 둘 이상인 탭에만 금고 안 폴더를 붙인다 — "문서" 두 개를 목록에서 가를 수 없다 (D-078).
    /// 열 때마다 새로 만든다. Tabs 를 두 번째 컨트롤에 묶지 않는다 (LL-007).
    /// </summary>
    public IReadOnlyList<TabListEntry> TabListEntries()
    {
        var counts = Tabs.GroupBy(t => t.TabTitle, StringComparer.OrdinalIgnoreCase)
                         .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return [.. Tabs.Select(t => new TabListEntry(t, counts[t.TabTitle] > 1 ? FolderOf(t) : null))];
    }

    /// 금고 기준 폴더. "열었을 때 상태" 탭은 원본 문서의 폴더 — 스냅숏 자리(.history)를 보이면 뜻이 없다.
    private string? FolderOf(EditorViewModel tab)
    {
        if (tab.CurrentPath is not { } path) return null;

        var document = tab.IsSnapshot ? PathRules.DocumentPathForSnapshot(_tree.Root, path) ?? path : path;
        if (Path.GetDirectoryName(document) is not { } folder) return null;

        var relative = Path.GetRelativePath(_tree.Root, folder);
        return relative == "." ? "금고 맨 위" : relative;
    }

    /// 탭이 하나도 없을 때 안내 문구를 띄운다. 빈 편집기를 보여주면 입력할 수 있다고 오해한다.
    public bool HasNoTabs => Tabs.Count == 0;

    /// 기존 바인딩·명령이 활성 탭을 가리키게 한다. 탭이 하나도 없으면 null 이다.
    public EditorViewModel? Editor => ActiveTab;

    public ObservableCollection<TreeNodeViewModel> Roots { get; } = [];

    public string RootPath => _tree.Root;

    public TreeNodeViewModel? Selected
    {
        get => _selected;
        set
        {
            // 코드가 선택을 옮기는 중이다(되돌리기 · RestoreSelection). TreeView 가 SelectedItemChanged 로 다시 들어오는데,
            // 여기서 되돌리기나 열기를 또 돌리면 선택이 null 로 굳어 트리에는 아무것도 안 보이는데
            // 명령은 다른 노드를 대상으로 한다 [실측 — 적대적 검토].
            if (_restoring) return;

            if (IsKeyBusy && value is not null && !ReferenceEquals(value, _selected))
            {
                // 키를 판정·전환하는 동안은 열지 않는다 — 옛 키로 탭이 열린 뒤 키가 바뀐다.
                // 선택을 되돌린다. 그대로 두면 끝난 뒤 같은 노드를 다시 눌러도 선택 변화가 없어 안 열린다 (J-5).
                var keep = _selected;
                MoveSelectionQuietly(() =>
                {
                    value.IsSelected = false;
                    if (keep is not null) keep.IsSelected = true;
                });
                _selected = keep;
                Raise(nameof(Selected));
                return;
            }

            if (!Set(ref _selected, value)) return;

            // 폴더 범위를 쓸 수 있는지가 선택에 딸려 바뀐다
            Raise(nameof(CanUseFolderScope));
            Guarded("문서 열기", OpenSelectedAsync);
        }
    }

    public RelayCommand NewFolderCommand { get; }
    public RelayCommand NewDocumentCommand { get; }
    public RelayCommand RenameCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand EmptyTrashCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand CopyAllCommand { get; }
    public RelayCommand ChangeRootCommand { get; }
    public RelayCommand OpenBackupCommand { get; }
    public RelayCommand CloseTabCommand { get; }
    public RelayCommand CloseOtherTabsCommand { get; }
    public RelayCommand CloseAllTabsCommand { get; }
    public RelayCommand CloseActiveTabCommand { get; }
    public RelayCommand OpenFindBarCommand { get; }
    public RelayCommand OpenSearchWindowCommand { get; }
    public RelayCommand SearchInFolderCommand { get; }
    public RelayCommand OpenInExplorerCommand { get; }
    public RelayCommand ChangeKeyCommand { get; }
    public RelayCommand LockCommand { get; }
    public RelayCommand ExitCommand { get; }

    // ── 음성 입력 (add-voice-input · D-180 · D-187) ──────────────────────────

    private IDictation? _dictation;

    /// 도구 막대 단추 · 편집 메뉴 · 단축키가 같은 명령이다. 듣는 중이면 어느 탭에서 눌러도 멈춘다.
    public RelayCommand ToggleDictationCommand { get; }

    /// <summary>
    /// 조립 루트가 붙인다 — 생성자 인자로 두지 않는다(시험의 셸 생성 코드 여러 곳을 그대로 두려고, D-187). 붙이지 않으면 명령이 꺼져 있다.
    /// </summary>
    public void UseDictation(IDictation dictation)
    {
        _dictation = dictation;
        dictation.StateChanged += (_, _) => RaiseDictation();
        RaiseDictation();
    }

    public bool IsDictating => _dictation?.State is DictationState.Listening;

    public string DictationMenuHeader => IsDictating ? "음성 입력 멈추기" : "음성 입력 시작";

    public string DictationButtonText => IsDictating ? "● 멈추기" : "말하기";

    /// 상태 줄 — 어느 문서에 들어가는지 함께 보인다 (plan R-9). 쉬면 빈 글자.
    public string DictationStatusText
    {
        get
        {
            if (_dictation is not { State: not DictationState.Idle } dictation) return "";

            var parts = new List<string>
            {
                dictation.State == DictationState.Listening ? $"● 듣는 중 {dictation.Elapsed:m\\:ss}" : "마무리 중",
                $"「{(dictation.Target as EditorViewModel)?.TabTitle}」"
            };
            if (dictation.IsPreparing) parts.Add("준비 중");
            if (dictation.Pending > 0) parts.Add($"받아쓰는 중 {dictation.Pending}");
            return string.Join(" · ", parts);
        }
    }

    private async Task ToggleDictationAsync()
    {
        if (_dictation is not { } dictation) return;

        if (dictation.State == DictationState.Listening) dictation.Stop();
        else if (dictation.State == DictationState.Idle && dictation.CanStart(ActiveTab)) await dictation.StartAsync(ActiveTab!);
    }

    private void RaiseDictation()
    {
        Raise(nameof(IsDictating));
        Raise(nameof(DictationMenuHeader));
        Raise(nameof(DictationButtonText));
        Raise(nameof(DictationStatusText));
        ToggleDictationCommand.RaiseCanExecuteChanged();
    }

    /// 도구 모음 「종료」 · Ctrl+Q. 창을 끄는 것은 View 의 일이다 — 트레이 「종료」와 같은 자리(MainWindow.RequestExit)로 간다.
    public event EventHandler? ExitRequested;

    // ── 단축키 (D-110~D-114) ─────────────────────────────────────────────────

    public RelayCommand OpenShortcutsCommand { get; }

    /// F1 · 도구 모음 「단축키」. 목록 창은 View 가 띄운다.
    public event EventHandler? ShortcutsRequested;

    /// 지금 키 표 — 동작 id → 키 글자(없으면 null). View 가 이것으로 창의 단축키를 만든다.
    public IReadOnlyDictionary<string, string?> Shortcuts { get; private set; }

    /// 키가 바뀌었다 — View 가 창의 단축키를 다시 만든다.
    public event EventHandler? ShortcutsChanged;

    /// 동작 id 의 명령. 목록에 없는 id 면 null.
    public System.Windows.Input.ICommand? CommandFor(string id) => id switch
    {
        "save" => SaveCommand,
        "closeTab" => CloseActiveTabCommand,
        "findBar" => OpenFindBarCommand,
        "searchVault" => OpenSearchWindowCommand,
        "findNext" => FindNextCommand,
        "findPrev" => FindPrevCommand,
        "closeFindBar" => CloseFindBarCommand,
        "refresh" => RefreshCommand,
        "shortcuts" => OpenShortcutsCommand,
        "exit" => ExitCommand,
        "newDocument" => NewDocumentCommand,
        "newFolder" => NewFolderCommand,
        "rename" => RenameCommand,
        "delete" => DeleteCommand,
        "copyAll" => CopyAllCommand,
        "openBackup" => OpenBackupCommand,
        "emptyTrash" => EmptyTrashCommand,
        "changeRoot" => ChangeRootCommand,
        "changeKey" => ChangeKeyCommand,
        "lock" => LockCommand,
        "closeAllTabs" => CloseAllTabsCommand,
        "openInExplorer" => OpenInExplorerCommand,
        "voiceInput" => ToggleDictationCommand,
        _ => null,
    };

    /// <summary>
    /// 새 키 표를 저장하고 바로 적용한다. 저장에 실패해도 이번 실행에는 적용한다 — 알리기만 한다.
    /// 겹침 · 막힌 키 검사는 목록 창(ShortcutsViewModel)이 먼저 한다.
    /// </summary>
    public async Task SetShortcutsAsync(IReadOnlyDictionary<string, string?> map)
    {
        Shortcuts = map;
        Raise(nameof(Shortcuts));                       // 위 메뉴 항목 오른쪽 키 글자가 따라온다 (D-159)
        _settings.Current.Shortcuts = map.ToDictionary(p => p.Key, p => p.Value ?? "");
        RaiseShortcutText();
        ShortcutsChanged?.Invoke(this, EventArgs.Empty);

        try { await _settings.SaveAsync(); }
        catch (Exception ex)
        {
            AppLog.Error("shortcut-save", null, ex);
            _dialogs.Error("단축키 저장 실패", "바꾼 키는 지금만 쓰이고, 다시 켜면 예전 키로 돌아갑니다.");
        }
    }

    private string KeySuffix(string id) => Shortcuts.TryGetValue(id, out var key) && key is not null ? $" ({key})" : "";

    // 툴팁에 적힌 키가 바꾼 키를 따라간다
    public string ExitToolTip => $"종료{KeySuffix("exit")} — 열린 문서를 저장하고 끕니다. 키도 지워집니다";
    public string CloseFindBarToolTip => $"닫기{KeySuffix("closeFindBar")}";
    public string FindNextToolTip => Shortcuts["findNext"] is { } key ? $"다음 (Enter · {key})" : "다음 (Enter)";
    public string FindPrevToolTip => Shortcuts["findPrev"] is { } key ? $"이전 (Shift+Enter · {key})" : "이전 (Shift+Enter)";
    public string ShortcutsToolTip => $"단축키 보기 · 바꾸기{KeySuffix("shortcuts")}";

    private void RaiseShortcutText()
    {
        Raise(nameof(ExitToolTip));
        Raise(nameof(CloseFindBarToolTip));
        Raise(nameof(FindNextToolTip));
        Raise(nameof(FindPrevToolTip));
        Raise(nameof(ShortcutsToolTip));
    }

    /// <summary>
    /// 창을 띄우는 것은 View 의 일이다 (D-013) — ViewModel 은 요청만 낸다.
    /// 값이 있으면 그 폴더를 범위로 잡고 연다(트리 우클릭 "이 폴더에서 찾기").
    /// </summary>
    public event EventHandler<string?>? SearchWindowRequested;
    public RelayCommand CloseFindBarCommand { get; }
    public RelayCommand FindNextCommand { get; }
    public RelayCommand FindPrevCommand { get; }

    private EditorViewModel? FindTab(string fullPath)
    {
        var key = PathRules.NormalizeFull(fullPath);
        return Tabs.FirstOrDefault(t => string.Equals(t.CurrentPath, key, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 탭 순서를 바꾼다. 반드시 <c>Move</c> 한 줄이어야 한다 —
    /// <c>Remove</c> + <c>Insert</c> 는 본문 TextBox 를 새로 만들어 실행취소 스택과 캐럿을
    /// 조용히 날리고, 활성 탭이면 보고 있던 문서까지 바뀐다 [실측].
    /// <see cref="RemoveTab"/> 도 타면 안 된다 — 편집기가 Dispose 된 채 목록에 남아
    /// 이후 모든 저장이 첫 줄에서 false 를 돌려주고 타이핑이 디스크에 한 글자도 안 남는다.
    /// </summary>
    public void MoveTab(EditorViewModel tab, int newIndex)
    {
        var from = Tabs.IndexOf(tab);
        if (from < 0) return;                    // 드래그가 진행되는 동안 닫힌 탭

        var to = Math.Clamp(newIndex, 0, Tabs.Count - 1);

        // 제자리 이동을 그대로 흘리면 TabControl 이 SelectedIndex=-1 을 쓰고
        // 양방향 바인딩이 ActiveTab=null 을 밀어 넣어 본문이 빈 화면이 된다 [실측].
        // 탭은 남아 있어 HasNoTabs 도 거짓이라 안내 문구조차 안 뜬다.
        // 제자리 드롭은 드래그에서 가장 흔한 헛손질이다.
        if (from == to) return;

        Tabs.Move(from, to);
    }

    /// <summary>
    /// 드롭 지점을 순서로 옮긴다. 대상 탭과 "그 앞인가 뒤인가"만 받는다 —
    /// 코드비하인드는 좌표를 푸는 데까지만 하고 인덱스 계산은 여기서 한다 (PROHIBITED-UI-03).
    /// 자기 자리가 먼저 비기 때문에 방향에 따라 한 칸씩 어긋난다.
    /// </summary>
    public void MoveTabTo(EditorViewModel dragged, EditorViewModel dropOn, bool after)
    {
        var from = Tabs.IndexOf(dragged);
        var to = Tabs.IndexOf(dropOn);
        if (from < 0 || to < 0) return;

        if (after && to < from) to++;
        else if (!after && to > from) to--;

        MoveTab(dragged, to);
    }

    // ── 검색 ─────────────────────────────────────────────────────────────────

    private SearchOutcome? _lastSearch;

    public SearchOutcome? LastSearch
    {
        get => _lastSearch;
        private set => Set(ref _lastSearch, value);
    }

    private SearchScope _lastSearchScope;

    /// 마지막으로 **실행한** 범위. 결과를 보는 중 트리 선택이 바뀌어도 이 값은 안 바뀐다.
    /// 알림이 없으면 결과 헤더의 범위 표시가 옛 값에 굳어 범위 착각을 만든다.
    public SearchScope LastSearchScope
    {
        get => _lastSearchScope;
        private set => Set(ref _lastSearchScope, value);
    }

    /// 폴더 범위를 쓸 수 있는가. 아무것도 안 고르면 대상이 루트라 전체와 같아진다.
    public bool CanUseFolderScope => Selected is not null && ScopeFolderFor(SearchScope.Folder) is not null;

    /// <summary>
    /// 이 범위의 대상 폴더. 전체·문서 범위이거나 대상이 루트면 null(=금고 전체).
    /// 폴더 판정은 <see cref="TargetFolder"/> 규칙 그대로다 — "새 폴더가 어디 생기나"를
    /// 이미 아는 사용자가 검색 범위도 예측할 수 있다.
    /// </summary>
    public string? ScopeFolderFor(SearchScope scope)
    {
        if (scope != SearchScope.Folder) return null;

        var folder = PathRules.NormalizeFull(TargetFolder());
        return string.Equals(folder, PathRules.NormalizeFull(_tree.Root), StringComparison.OrdinalIgnoreCase)
            ? null
            : folder;
    }

    /// <summary>
    /// 본문 검색. 새로 시작하면 돌고 있던 검색을 취소한다 —
    /// 겹쳐 돌면 늦게 끝난 쪽이 먼저 낸 결과를 덮어써 엉뚱한 목록이 남는다.
    /// </summary>
    /// <param name="scopeTarget">
    /// 검색 창에서 명시적으로 고른 대상 — 폴더 범위면 폴더 경로, 문서 범위면 문서 경로.
    /// 주면 트리 선택·활성 탭 대신 이걸 쓴다. 창이 떠 있는 동안 트리 선택이 바뀌어도
    /// 범위가 발밑에서 안 바뀐다.
    /// </param>
    public async Task<SearchOutcome> RunSearchAsync(string query, SearchScope scope, bool matchCase = false,
                                                    string? scopeTarget = null)
    {
        // 키를 바꾸는 중에 돌면 한 번의 검색 안에서 키가 섞인다. 던지지 않고 취소로 돌려준다 —
        // 검색 창은 결과 Task 를 기다리지 않아 예외가 관측되지 않는다.
        if (IsKeyBusy) return SearchOutcome.Empty with { Canceled = true };

        var previous = _searchCts;
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        // 해제는 하지 않는다 — 아직 돌고 있는 검색이 그 토큰을 보고 있다.
        // 각자 자기 것을 자기 finally 에서 해제한다.
        if (previous is not null) await previous.CancelAsync();

        // 폴더 범위인데 대상 폴더가 없으면 실제로는 금고 전체를 훑는다.
        // 그걸 '폴더에서 찾았다'고 적으면 범위 착각이 그대로 난다.
        var folder = scope == SearchScope.Folder && scopeTarget is not null
            ? PathRules.NormalizeFull(scopeTarget)
            : ScopeFolderFor(scope);
        var effective = scope == SearchScope.Folder && folder is null ? SearchScope.All : scope;

        try
        {
            var outcome = effective == SearchScope.Document
                ? await SearchOneDocumentAsync(scopeTarget, query, matchCase)
                : await _search.SearchAsync(query, folder, OpenTabTexts(), cts.Token, matchCase);

            // 취소됐거나, 도는 사이에 다른 검색이 시작됐으면 화면을 건드리지 않는다.
            // 취소 신호가 마지막 문서를 처리하는 중에 도착하면 Canceled=false 로 끝날 수 있어
            // (더 돌 문서가 없어 루프가 정상 종료한다), Canceled 만 봐서는 못 막는다 [실측].
            if (outcome.Canceled || !ReferenceEquals(_searchCts, cts))
                return outcome with { Canceled = true };

            LastSearch = outcome;
            LastSearchScope = effective;
            return outcome;
        }
        finally
        {
            // 널로 만든 뒤에 해제한다 — 그 사이 CancelSearch 가 해제된 것을 잡으면 터진다
            if (ReferenceEquals(_searchCts, cts)) _searchCts = null;
            cts.Dispose();
        }
    }

    /// 이름 검색. 복호화가 0회라 입력 즉시 돌려도 된다.
    public Task<IReadOnlyList<SearchHit>> SearchNamesAsync(string query, SearchScope scope, bool matchCase = false,
                                                           string? folderOverride = null)
        => _search.SearchNamesAsync(
            query,
            scope == SearchScope.Folder && folderOverride is not null
                ? PathRules.NormalizeFull(folderOverride)
                : ScopeFolderFor(scope),
            CancellationToken.None, matchCase);

    /// <summary>
    /// 트리 선택을 이 경로로 옮긴다. 세터를 우회하므로 문서 열기 경로를 다시 타지 않는다 —
    /// 검색 결과에서 폴더를 골라 범위로 쓸 때처럼, 선택만 옮기고 싶은 경우에 쓴다.
    /// </summary>
    public void SelectPath(string fullPath)
    {
        RestoreSelection(PathRules.NormalizeFull(fullPath));
        Raise(nameof(CanUseFolderScope));
    }

    /// <summary>
    /// 검색할 문서를 고른다. 열려 있지 않아도 된다 — 열린 탭 중에서만 고를 수 있으면
    /// "금고에 없다"는 거짓 답이 나온다.
    /// 금고 밖과 예약 영역은 거부한다. 취소하거나 거부되면 null.
    /// </summary>
    public string? PickVaultDocument(string? current)
    {
        var start = current is not null
            ? Path.GetDirectoryName(current) ?? _tree.Root
            : _tree.Root;

        var picked = _dialogs.PickDocument(start, "검색할 문서 선택");
        if (picked is null) return null;

        return ValidateScopeTarget(picked, "검색할 수 없는 문서");
    }

    /// <summary>
    /// 검색 범위로 쓸 폴더를 고른다. 금고 밖과 예약 영역(.trash/.history)은 거부한다 —
    /// 밖은 아예 못 읽고, 예약 영역은 지운 비밀과 편집 전 값이 들어 있다.
    /// 취소하거나 거부되면 null.
    /// </summary>
    public string? PickScopeFolder(string? current)
    {
        var picked = _dialogs.PickFolder(current ?? _tree.Root, "검색할 폴더 선택");
        if (picked is null) return null;

        return ValidateScopeTarget(picked, "검색할 수 없는 폴더");
    }

    /// 금고 밖과 예약 영역(.trash/.history)을 거른다. 통과하면 정규화된 경로.
    private string? ValidateScopeTarget(string picked, string rejectTitle)
    {
        var full = PathRules.NormalizeFull(picked);

        if (!PathRules.IsInsideRoot(_tree.Root, full))
        {
            _dialogs.Error("범위 밖", $"금고 안에서만 고를 수 있습니다.\n\n금고: {_tree.Root}");
            return null;
        }

        if (PathRules.IsReservedArea(_tree.Root, full))
        {
            _dialogs.Error(rejectTitle,
                "휴지통과 이전 세대 기록은 검색하지 않습니다.\n지운 문서와 바꾸기 전 값이 들어 있는 곳입니다.");
            return null;
        }

        return full;
    }

    public void CancelSearch() => _searchCts?.Cancel();

    /// <summary>
    /// 검색 결과를 연다. 이미 열려 있으면 활성화만 한다 (C-05) —
    /// 다시 로드하면 opened.tbx(유일한 되돌릴 지점)가 덮인다.
    /// 연 뒤에는 그 문서에 검색어를 걸어 둔다. 안 그러면 사용자가 긴 문서에서 다시 찾아야 한다.
    /// </summary>
    public async Task OpenHitAsync(SearchHit hit, string query)
    {
        await OpenAsync(hit.FullPath);

        ActiveTab?.SetSearch(query);
        RestoreSelection(PathRules.NormalizeFull(hit.FullPath));
    }

    /// 검색 창의 진입점. 예외는 Guarded 가 알린다 — 결과 Task 를 버리면 열리지 않은 이유가
    /// 대화상자에도 로그에도 남지 않는다 (링크로 막힌 경우 등).
    public Task OpenHitGuardedAsync(SearchHit hit, string query)
    {
        Guarded("문서 열기", () => OpenHitAsync(hit, query));
        return Pending;
    }

    /// <summary>
    /// 열린 탭의 메모리 본문. 자동 저장은 1.5초 디바운스라 디스크가 그만큼 낡아 있다 —
    /// 이게 없으면 방금 친 값이 "없음"으로 보고된다 [실측].
    /// 읽기 전용 탭(스냅샷·읽기 실패)은 본문이 원본과 다르므로 제외한다.
    /// </summary>
    private Dictionary<string, string> OpenTabTexts()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tab in Tabs)
        {
            if (tab.IsReadOnly || tab.CurrentPath is null) continue;
            map[tab.CurrentPath] = tab.Text;
        }
        return map;
    }

    /// <summary>
    /// 문서 하나를 뒤진다. 고른 문서가 열려 있으면 **메모리 본문**을 쓴다 —
    /// 자동 저장은 1.5초 디바운스라 디스크가 그만큼 낡아 방금 친 값이 "없음"이 된다.
    /// 열려 있지 않으면 디스크에서 읽는다. 대상을 안 주면 지금 보는 탭이다.
    /// </summary>
    private async Task<SearchOutcome> SearchOneDocumentAsync(string? path, string query, bool matchCase)
    {
        var target = path is not null ? PathRules.NormalizeFull(path) : ActiveTab?.CurrentPath;
        if (target is null) return SearchOutcome.Empty;

        var tab = FindTab(target);

        // 열려 있으면 강조도 같이 걸어 둔다. 검색어를 비우면 강조도 지워야 한다 —
        // 남으면 화면에 어느 값이 민감한지 표시해 두는 셈이 된다.
        if (tab is not null)
        {
            tab.MatchCase = matchCase;
            tab.SetSearch(query);
        }

        if (string.IsNullOrWhiteSpace(query)) return SearchOutcome.Empty;

        // 읽지 못한 문서를 '뒤져봤고 없었다'로 보고하면 안 된다. 전체 범위는 1건으로 세는데
        // 범위에 따라 정직함이 갈리면 사용자는 없다고 믿는다 (C-07).
        // 다른 키 문서는 "읽지 못한 문서"와 따로 센다 — 전체 범위와 문구가 갈리면 안 된다.
        if (tab is { LoadFailed: true }) return SearchOutcome.NotRead(tab.LoadStatus);

        // 열려 있고 읽을 수 있으면 메모리 본문을, 아니면 디스크를 쓴다.
        // 세는 규칙은 서비스 한 곳에 둔다 — 두 벌이면 "창에서는 3건인데 문서 안에서는 2건"이 된다.
        var live = tab is { IsReadOnly: false } ? tab.Text : null;

        return await _search.SearchDocumentAsync(target, query, live, CancellationToken.None, matchCase);
    }

    /// <summary>
    /// 열려 있는 모든 탭의 검색 강조를 지운다. 검색 결과로 여러 탭을 열면
    /// 강조가 무기한 남아 화면에 어느 값이 민감한지 표시해 두는 셈이 된다.
    /// </summary>
    public void ClearSearchHighlights()
    {
        foreach (var tab in Tabs) tab.ClearSearch();
    }

    // ── 문서 안에서 찾기 띠 (Ctrl+F) ─────────────────────────────────────────

    private bool _isFindBarOpen;

    /// 띠는 창에 하나다. 탭마다 두면 전환할 때마다 열렸다 닫혔다 한다.
    public bool IsFindBarOpen
    {
        get => _isFindBarOpen;
        private set => Set(ref _isFindBarOpen, value);
    }

    private void OpenFindBar() => IsFindBarOpen = true;

    private void CloseFindBar()
    {
        IsFindBarOpen = false;

        // 닫으면 강조도 지운다 — 남으면 화면에 어느 값이 민감한지 표시해 두는 셈이 된다
        ActiveTab?.ClearSearch();
    }

    /// 이 경로(또는 그 하위)를 열고 있는 탭 전부. 활성 탭만 보면 배경 탭이 옛 경로를 쥔 채 남는다 (F-4).
    private List<EditorViewModel> TabsAffectedBy(string fullPath)
        => [.. Tabs.Where(t => t.IsAffectedBy(fullPath))];

    /// <summary>
    /// 이 문서(또는 폴더 안 문서들)의 이전 세대를 보고 있는 탭.
    /// 스냅샷은 .history 밑이라 <see cref="TabsAffectedBy"/> 로는 잡히지 않는다 —
    /// 그래서 경로 갱신 대상이 아니고, 원본이 옮겨지거나 지워지면 닫는다 (J-2).
    /// </summary>
    private List<EditorViewModel> SnapshotTabsFor(string fullPath)
    {
        var history = PathRules.HistoryFolderFor(_tree.Root, fullPath);
        return [.. Tabs.Where(t => t.IsSnapshot && t.CurrentPath is not null
                                   && PathRules.IsInsideRoot(history, t.CurrentPath))];
    }

    /// <summary>
    /// 디스크를 건드리기 전에 영향 탭을 먼저 저장하고 예약을 끈다 (J-1, F-4).
    /// 하나라도 실패하면 동작을 취소하고 어느 문서 때문인지 말한다 —
    /// 이름만 바꿔 두면 탭이 옛 경로를 쥔 채 남아 저장이 영영 실패한다.
    /// </summary>
    private async Task<bool> PrepareAffectedTabsAsync(IReadOnlyList<EditorViewModel> affected, string action)
    {
        foreach (var tab in affected)
        {
            if (!await tab.TrySaveForLeaveAsync())
            {
                ActiveTab = tab;
                _dialogs.Error($"{action} 불가",
                    $"'{tab.TabTitle}' 을(를) 저장하지 못했습니다. 먼저 해결한 뒤 다시 시도해주세요.");
                return false;
            }

            // 예약이 살아 있으면 디스크 작업 뒤 옛 경로로 나가 유령 문서를 만든다
            tab.CancelPendingSave();
        }
        return true;
    }

    /// 옮겨지거나 이름이 바뀐 자리를 따라간다. 문서 자신이면 그대로, 조상 폴더면 상대 경로를 이어 붙인다.
    private static void FollowPath(EditorViewModel tab, string oldPath, string newPath)
    {
        var relative = Path.GetRelativePath(oldPath, tab.CurrentPath!);
        tab.UpdatePath(relative == "." ? newPath : Path.Combine(newPath, relative));
    }

    /// <summary>
    /// 문서를 탭으로 연다. 이미 열려 있으면 활성화만 한다 (C-05) —
    /// 다시 로드하면 opened.tbx 가 현재 내용으로 덮여 유일한 되돌릴 지점이 사라진다 (D-016, F-3).
    /// </summary>
    public async Task OpenAsync(string fullPath)
    {
        if (FindTab(fullPath) is { } existing)
        {
            ActiveTab = existing;
            return;
        }

        var editor = _editorFactory.Create();
        await editor.LoadAsync(fullPath);
        Tabs.Add(editor);
        ActiveTab = editor;
    }

    /// <summary>
    /// 탭을 닫는다. 저장에 실패하면 닫지 않는다 (C-03, D-011) —
    /// 닫아 버리면 편집 내용이 어디에도 남지 않는다.
    /// </summary>
    public async Task<bool> CloseTabAsync(EditorViewModel tab)
    {
        if (!await tab.TrySaveForLeaveAsync())
        {
            // 닫기 버튼은 그 탭을 활성화하지 않는다 [실측].
            // 안 보이는 문서의 실패 사유를 받으면 무엇을 고쳐야 할지 알 수 없다.
            ActiveTab = tab;

            if (ConfirmDiscardBlocked(tab))
            {
                RemoveTab(tab);
                return true;
            }

            _dialogs.Error("닫을 수 없음", $"'{tab.TabTitle}' 을(를) 저장하지 못해 닫지 않았습니다.");
            return false;
        }

        RemoveTab(tab);
        return true;
    }

    /// <summary>
    /// 키 때문에 저장이 거부된 탭(다른 키 파일 · 키 전환 뒤)은 다시 해도 영영 실패한다.
    /// 닫기·종료를 막기만 하면(D-011) 앱을 강제로 끄는 수밖에 없고, 그러면 편집 내용도 사라진다.
    /// 그래서 이 탭에만 "편집 내용을 버리고 닫기"를 두 번 확인해 연다. 본문 복사는 그 전에 할 수 있다.
    /// </summary>
    private bool ConfirmDiscardBlocked(EditorViewModel tab)
    {
        if (!tab.SaveBlockedByKey) return false;

        if (!_dialogs.Confirm("저장할 수 없는 문서",
                $"'{tab.TabTitle}' 은(는) 파일이 지금 키로 잠긴 문서가 아니어서 저장할 수 없습니다.\n\n"
                + "필요한 내용은 먼저 복사해 두세요. 편집 내용을 버리고 닫을까요?")) return false;

        return _dialogs.Confirm("편집 내용 버리기", "버린 편집 내용은 되돌릴 수 없습니다. 정말 버리고 닫을까요?");
    }

    /// <summary>
    /// 우클릭한 탭 하나만 남기고 나머지를 닫는다 (C-01).
    /// 기준을 활성 탭으로 잡으면 배경 탭을 우클릭했을 때 남길 탭과 닫을 탭이 통째로 뒤바뀐다.
    /// </summary>
    public Task<bool> CloseOtherTabsAsync(EditorViewModel keep)
    {
        // 남길 탭을 먼저 활성으로 세운다 — 나중에 하면 닫는 동안 활성 탭이 여러 번 옮겨 다닌다
        ActiveTab = keep;
        return CloseManyAsync([.. Tabs.Where(t => !ReferenceEquals(t, keep))], "다른 탭 닫기");
    }

    public Task<bool> CloseAllTabsRequestedAsync() => CloseManyAsync([.. Tabs], "모든 탭 닫기");

    /// <summary>
    /// 여러 탭을 닫는 유일한 경로. 2단 구조다 — ① 전부 저장 시도 → ② 저장된 것만 제거.
    /// 하나씩 "저장하고 닫기"를 반복하면 중간에 실패했을 때 앞의 것은 이미 닫힌 채로 멈추고,
    /// 그 상태는 되돌릴 수 없다.
    /// <paramref name="quiet"/> 면 대화상자를 띄우지 않는다(절전 잠그기) — 실패는 로그만 남긴다.
    /// </summary>
    private async Task<bool> CloseManyAsync(IReadOnlyList<EditorViewModel> targets, string action, bool quiet = false)
    {
        if (_closingMany) return false;      // await 도중 메뉴를 다시 눌러도 겹치지 않는다
        _closingMany = true;

        try
        {
            var failed = new List<EditorViewModel>();

            // ① 알림 없는 저장. 편집기가 각자 띄우면 모달이 N개 겹쳐 뜬다.
            foreach (var tab in targets)
            {
                if (!await tab.TrySaveForLeaveAsync()) failed.Add(tab);
            }

            // 조용히 닫을 때는 ①에서 하나라도 못 저장하면 아무것도 닫지 않는다. 물을 수 없는 자리라
            // 일부만 닫히고 키는 남는 어중간한 상태를 만들지 않는다 (D-100). 다만 ② 재확인은 ①의 await 사이에
            // 입력이 들어온 탭만 다시 저장하는데, 그것이 실패하면 앞 탭은 이미 닫혔다 — 그때도 키는 남긴다(fail-open)
            if (quiet && failed.Count > 0)
            {
                foreach (var tab in failed) AppLog.Warn(action, tab.CurrentPath, null);
                return false;
            }

            foreach (var tab in targets)
            {
                if (failed.Contains(tab)) continue;

                // ② 다시 확인한다 — ①의 await 동안 사용자가 그 탭에 입력했을 수 있다.
                //    WPF 는 await 중에도 입력을 처리한다. 판정 결과만 믿고 해제하면
                //    그 입력은 파일에도 메모리에도 남지 않는다. 이미 저장된 탭은 즉시 통과라 비용이 없다.
                if (!await tab.TrySaveForLeaveAsync()) { failed.Add(tab); continue; }

                RemoveTab(tab);
            }

            // 탭이 남아 있으면 트리 선택이 방금 닫힌 문서를 가리킨 채로 남는다 —
            // 그 문서를 다시 클릭해도 SelectedItemChanged 가 안 나서 안 열린다 (J-5 의 새 형태).
            // RemoveTab 의 정리는 탭이 0이 될 때만 돈다.
            // 키 때문에 저장이 막힌 탭은 버릴지 묻는다 — 다시 해도 영영 실패한다
            foreach (var tab in failed.ToList())
            {
                if (quiet || !tab.SaveBlockedByKey) continue;

                ActiveTab = tab;
                if (!ConfirmDiscardBlocked(tab)) continue;

                failed.Remove(tab);
                RemoveTab(tab);
            }

            if (Tabs.Count > 0) RestoreSelection(ActiveTab?.CurrentPath);

            if (failed.Count == 0) return true;

            if (quiet)
            {
                foreach (var tab in failed) AppLog.Warn(action, tab.CurrentPath, null);
                return false;
            }

            ActiveTab = failed[0];
            _dialogs.Error($"{action} 일부 실패",
                $"저장하지 못해 닫지 않은 문서가 있습니다.\n\n{string.Join(", ", failed.Select(t => t.TabTitle))}");
            return false;
        }
        finally { _closingMany = false; }
    }

    /// 목록에서 빼고 해제한다. 해제를 빠뜨리면 닫힌 탭이 1.5초 뒤 디스크에 쓰고
    /// 복호화된 평문이 종료까지 남는다 (F-7).
    private void RemoveTab(EditorViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        Tabs.RemoveAt(index);
        tab.Dispose();

        if (Tabs.Count > 0)
        {
            // 닫은 자리를 이어받는다. 앞으로 보내면 배경 탭을 닫았을 때 보던 문서가 바뀐다.
            if (ReferenceEquals(_activeTab, tab)) ActiveTab = Tabs[Math.Min(index, Tabs.Count - 1)];
            return;
        }

        ActiveTab = null;

        // 안 비우면 같은 문서를 다시 클릭해도 SelectedItemChanged 가 안 나서 안 열린다 (J-5).
        // 노드의 IsSelected 까지 꺼야 한다 — TreeViewItem 이 선택된 채 남으면 같은 증상이 그대로다.
        if (_selected is not null) _selected.IsSelected = false;
        _selected = null;
        Raise(nameof(Selected));
    }

    public Task RefreshAsync() => RefreshAsync(null);

    /// <summary>
    /// selectPath 를 주면 그 경로를 선택한다. 이동처럼 선택 대상이 새 경로로 바뀌는 경우에 쓴다 —
    /// 옛 경로를 쥐고 있으면 다음 "새 폴더"가 방금 비운 자리를 디스크에 되살린다.
    /// </summary>
    public async Task RefreshAsync(string? selectPath)
    {
        // 트리를 다시 그리면 노드 객체가 전부 새로 만들어져 선택이 풀린다.
        // 그대로 두면 폴더를 하나 만든 직후 선택이 사라져, 이어서 만드는 것이 최상위로 간다.
        var previous = selectPath ?? Selected?.FullPath;

        var root = await _tree.ScanAsync();
        Roots.Clear();
        Roots.Add(new TreeNodeViewModel(root) { IsRoot = true });
        Raise(nameof(RootPath));

        if (previous is not null) RestoreSelection(previous);

        await RefreshKeyStatesAsync();
    }

    /// <summary>
    /// 드롭 한 번이 건드리는 것: 열린 문서 경로, 예약된 자동 저장, .history 하위 트리,
    /// 새로고침 뒤 선택. 순서를 지키지 않으면 조용히 데이터를 만들거나 지운다.
    /// </summary>
    public async Task<bool> TryMoveAsync(string sourceFullPath, string destinationFolder)
    {
        var check = _tree.CheckMove(sourceFullPath, destinationFolder);

        // 같은 자리 드롭은 가장 흔한 헛손질이다. 매번 대화상자를 띄우면 못 쓴다 (J-3)
        if (check.Reason == MoveRejection.SameLocation) return false;

        if (!check.CanMove)
        {
            _dialogs.Error("옮길 수 없음", check.Message);
            return false;
        }

        // 영향 탭만 저장하고 예약을 끈다. 전에는 무관한 탭의 예약까지 무조건 죽였다 —
        // 입력 중이던 다른 문서가 조용히 저장되지 않는 버그였다.
        var affected = TabsAffectedBy(sourceFullPath);
        if (!await PrepareAffectedTabsAsync(affected, "옮기기")) return false;

        string moved;
        try
        {
            moved = _tree.Move(sourceFullPath, destinationFolder);
        }
        catch (Exception ex)
        {
            // DirectoryNotFoundException 을 Guarded 에 흘리면 "금고 폴더 없음"으로 오역된다
            AppLog.Error("move", sourceFullPath, ex);
            _dialogs.Error("옮기기 실패", ErrorText.For(ex, "항목을 옮기지 못했습니다."));
            return false;
        }

        // 경로를 갱신하지 않으면 자동 저장이 옛 자리에 유령 문서를 만든다
        foreach (var tab in affected) FollowPath(tab, sourceFullPath, moved);

        // 이전 세대 탭은 원본을 따라가지 않는다 (J-2)
        foreach (var snapshot in SnapshotTabsFor(sourceFullPath)) RemoveTab(snapshot);

        await RefreshAsync(moved);
        return true;
    }

    private string _dropHint = "";

    /// 드래그 중 보여줄 거부 사유. 금지 커서만으로는 이름이 겹쳐서인지, 자기 하위라서인지,
    /// 경로가 길어서인지 구분할 수 없다 — 사용자는 "왜 안 되는지 모르는 실패"를 반복하게 된다.
    public string DropHint
    {
        get => _dropHint;
        private set => Set(ref _dropHint, value);
    }

    /// <summary>
    /// View 가 드래그 중 커서를 정할 때 쓴다. 판정은 서비스가 한다 (PROHIBITED-UI-03).
    /// 거부면 사유를 <see cref="DropHint"/> 에 실어 상태표시줄이 설명하게 한다.
    /// 드래그오버에서 거부하면 Windows 가 드롭 자체를 전달하지 않아, 대화상자로는 사유가 영영 안 보인다.
    /// </summary>
    public bool EvaluateDrop(string sourceFullPath, string destinationFolder)
    {
        if (IsKeyBusy)
        {
            DropHint = "키를 확인하는 중에는 옮길 수 없습니다";
            return false;
        }

        var check = _tree.CheckMove(sourceFullPath, destinationFolder);
        DropHint = check.CanMove ? "" : check.Message;
        return check.CanMove;
    }

    public void ClearDropHint() => DropHint = "";

    /// 드롭 진입점. 예외는 Guarded 안에서 처리된다 — 코드비하인드에 async void 를 두지 않는다.
    public Task DropAsync(string sourceFullPath, string destinationFolder)
    {
        Guarded("옮기기", async () => { await TryMoveAsync(sourceFullPath, destinationFolder); });
        return Pending;
    }

    /// <summary>
    /// 가장 최근에 시작된 비동기 작업. 예외는 <see cref="Guarded"/> 안에서 이미 처리되므로
    /// 이 Task 는 실패하지 않는다. 테스트가 폴링 대신 이것을 기다린다.
    /// </summary>
    public Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// 비동기 명령에서 예외가 새어 앱이 종료되는 것을 막는다.
    /// 루트가 사라진 경우는 오류로 끝내지 않고 재지정 흐름으로 보낸다 — 앱 안에서 복구할 길을 남긴다.
    /// async void 를 쓰지 않는다 — Task 를 붙들어야 관측도 되고 테스트도 기다릴 수 있다.
    /// </summary>
    private void Guarded(string action, Func<Task> body)
    {
        // 키를 판정·전환·잠그는 동안에는 명령을 받지 않는다. 옛 키로 탭이 열리면
        // 세대가 바뀐 뒤 그 탭의 저장이 영영 거부된다.
        if (IsKeyBusy) return;

        Pending = GuardedCore(action, body);
    }

    private async Task GuardedCore(string action, Func<Task> body)
    {
        try
        {
            await body();
        }
        catch (DirectoryNotFoundException ex) when (_tree.RootExists())
        {
            // 금고는 그대로 있고 그 아래 항목이 밖에서 지워졌다. 재지정 창을 띄우면
            // 사용자는 금고 전체가 사라졌다고 오해한다 (D-041).
            AppLog.Error(action, null, ex);
            _dialogs.Error($"{action} 실패",
                "이 항목을 찾을 수 없습니다.\n\n밖에서 이름이 바뀌었거나 지워졌을 수 있습니다. 도구 모음 [새로고침](기본 F5)으로 새로고침하세요.");
        }
        catch (DirectoryNotFoundException)
        {
            _dialogs.Error("금고 폴더 없음", $"금고 폴더를 찾을 수 없습니다.\n\n{_tree.Root}\n\n폴더를 다시 지정해주세요.");
            await RepickRootAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error(action, null, ex);
            _dialogs.Error($"{action} 실패", ErrorText.For(ex, "작업을 마치지 못했습니다."));
        }
    }

    private async Task RepickRootAsync()
    {
        var picked = _dialogs.PickFolder(_tree.Root, "금고 폴더 선택");
        if (picked is null) return;

        await ApplyRootAsync(picked);
    }

    private async Task ApplyRootAsync(string picked)
    {
        // 옛 금고의 탭에 받아쓴 글자가 들어가지 않게 — 남은 조각은 버린다 (D-186)
        _dictation?.Cancel();

        // 새 금고를 그린 뒤 키를 다시 판정해 새 세대를 세우기까지 명령을 막는다. 그 사이 연 탭은 옛 세대에 묶여
        // 판정이 끝나는 순간부터 저장이 영영 거부된다 [실측 — 적대적 검토].
        using var busy = BeginBusy();

        // 인스턴스를 그대로 두고 루트만 바꾼다. DocumentStore와 EditorViewModel이
        // 붙잡고 있는 것이 같은 인스턴스라 함께 따라온다.
        _tree.SetRoot(picked);
        _settings.Current.RootPath = picked;
        await _settings.SaveAsync();

        // 옛 금고를 가리키는 탭이 남으면 저장 경로가 막힌 채 조용한 실패 상태가 된다 (J-4)
        CloseAllTabs();
        await RefreshAsync();

        // 변경·재지정 공통. 새 금고로 키를 다시 판정한다
        await EnsureKeyForVaultAsync(afterRootChange: true);
    }

    private void CloseAllTabs()
    {
        foreach (var tab in Tabs) tab.Dispose();
        Tabs.Clear();
        ActiveTab = null;
    }

    /// <summary>
    /// 창을 닫기 전에 부른다. 활성 탭만 저장하면 배경 탭의 편집 내용이 종료와 함께 사라진다 (J-3).
    /// 하나라도 실패하면 false — 호출자가 종료를 취소한다 (D-011).
    /// </summary>
    public Task<bool> SaveAllForExitAsync()
    {
        // 저장 뒤에 받아쓴 글자가 들어오면 저장되지 않은 채 꺼진다 — 저장 전에 버린다 (D-186, plan R-2)
        _dictation?.Cancel();
        return SaveAllTabsAsync("종료");
    }

    /// <summary>
    /// ✕ 로 트레이에 숨기기 전에 View 가 부른다. 숨은 앱은 Windows 종료를 막지도 묻지도 못한다 [문서] —
    /// 숨는 순간이 마지막 확인 자리라 전부 저장하고, 하나라도 못 하면 숨기지 않는다 (D-095).
    /// 처음 한 번은 어디로 숨는지 · 어떻게 끄는지 알린다 (D-096). 예외를 내지 않는다 — View 는 결과만 본다.
    /// </summary>
    public async Task<bool> PrepareHideAsync()
    {
        try
        {
            // 키 전환 도중에 숨기면 전환 저장과 숨기기 저장이 같은 탭을 동시에 저장한다 — 닫기와 같은 이유
            if (IsKeyBusy)
            {
                WarnBusy();
                return false;
            }

            // 여러 탭을 닫는 중(저장을 기다리는 중)이면 겹치지 않는다. 그 흐름이 끝난 뒤 다시 누르면 된다
            if (_closingMany) return false;

            if (!_settings.Current.TrayNoticeShown)
            {
                if (!_dialogs.ConfirmHideToTray()) return false;
                await RememberTrayNoticeAsync();
            }

            // 보이지 않는 창에서 마이크가 계속 켜져 있지 않게 듣기를 멈춘다. 말하던 조각까지는 넣는다 (D-185)
            _dictation?.Stop();

            return await SaveAllTabsAsync("숨기기", allowDiscard: false);
        }
        catch (Exception ex)
        {
            AppLog.Error("숨기기", null, ex);
            _dialogs.Error("숨기기 실패", ErrorText.For(ex, "창을 숨기지 못했습니다."));
            return false;
        }
    }

    private async Task RememberTrayNoticeAsync()
    {
        _settings.Current.TrayNoticeShown = true;

        // 못 쓰면 다음에 한 번 더 묻는 정도의 손해다 — 이것 때문에 숨기기를 막지 않는다
        try { await _settings.SaveAsync(); }
        catch (Exception ex) { AppLog.Warn("settings-save", null, ex); }
    }

    /// <summary>
    /// Windows 종료 · 로그아웃 때 부른다. 대화상자를 띄우지 않는다 — 사용자가 답할 수 없는 자리이고,
    /// 답을 기다리는 사이 Windows 가 앱을 끊는다 (D-098). 실패는 로그(경로)만 남긴다.
    /// </summary>
    public async Task<bool> SaveAllQuietlyAsync()
    {
        // 저장 뒤에 받아쓴 글자가 들어오면 저장되지 않은 채 꺼진다 — 저장 전에 버린다 (D-186)
        _dictation?.Cancel();

        var failed = new List<EditorViewModel>();

        // 뒤 탭을 저장하는 사이 앞 탭에 친 글자가 있으면 한 바퀴 더 돈다 — SaveAllTabsAsync 와 같은 이유
        for (var pass = 0; pass < 2; pass++)
        {
            failed.Clear();
            foreach (var tab in Tabs.ToList())
            {
                if (!await tab.TrySaveForLeaveAsync()) failed.Add(tab);
            }

            if (!Tabs.Any(t => !failed.Contains(t) && t.IsDirty && !t.IsReadOnly)) break;
        }

        foreach (var tab in failed) AppLog.Warn("session-save", tab.CurrentPath, null);
        return failed.Count == 0;
    }

    /// <summary>
    /// 하나라도 저장에 실패하면 동작을 취소하고 어느 문서 때문인지 말한다 (J-1·J-3·J-4).
    /// <paramref name="allowDiscard"/> 가 false 면 키 때문에 막힌 탭도 버리기를 묻지 않고 멈춘다 —
    /// 버리기 확인은 "곧 전부 해제된다"(종료 · 금고 변경)를 전제로 한다. 숨기기는 탭이 그대로 남는다 (D-095).
    /// </summary>
    private async Task<bool> SaveAllTabsAsync(string action, bool allowDiscard = true)
    {
        var discarded = new HashSet<EditorViewModel>();

        // 뒤 탭을 저장하는 사이 앞 탭(보던 탭)에 친 글자는 그 탭의 저장이 이미 끝나 다시 보지 않았다 —
        // 종료하면 사라진다 [실측 — 적대적 검토 2차]. 수정된 탭이 남으면 한 바퀴 더 돈다. 계속 바뀌면 멈춘다.
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var tab in Tabs.ToList())
            {
                if (discarded.Contains(tab)) continue;
                if (await tab.TrySaveForLeaveAsync()) continue;

                ActiveTab = tab;

                if (!allowDiscard && tab.SaveBlockedByKey)
                {
                    _dialogs.Error($"{action} 불가",
                        $"'{tab.TabTitle}' 은(는) 파일이 지금 키로 잠긴 문서가 아니어서 저장할 수 없습니다.\n\n"
                        + "필요한 내용을 복사하고 탭을 닫은 뒤 다시 시도해주세요.");
                    return false;
                }

                // 버리기로 했으면 이 탭 때문에 멈추지 않는다 — 곧 전부 해제된다 (종료 · 금고 변경)
                if (ConfirmDiscardBlocked(tab))
                {
                    discarded.Add(tab);
                    continue;
                }

                _dialogs.Error($"{action} 불가",
                    $"'{tab.TabTitle}' 을(를) 저장하지 못했습니다. 먼저 해결한 뒤 다시 시도해주세요.");
                return false;
            }

            if (!Tabs.Any(t => !discarded.Contains(t) && t.IsDirty && !t.IsReadOnly)) return true;
        }

        _dialogs.Error($"{action} 불가", "저장하는 동안 입력이 계속 들어와 저장을 마치지 못했습니다. 잠시 뒤 다시 시도해주세요.");
        return false;
    }

    /// <summary>
    /// 전환 실패 되돌리기가 없어졌다. 탭이 있으면 이전 문서가 탭에 그대로 남으므로
    /// "전환 때문에 편집 내용이 사라지는" 상황(D-011) 자체가 성립하지 않는다.
    /// </summary>
    private async Task OpenSelectedAsync()
    {
        if (Selected is null || Selected.IsFolder) return;

        await OpenAsync(Selected.FullPath);
    }

    private void RestoreSelection(string? openedPath)
    {
        if (openedPath is null) return;

        var node = FindByPath(Roots, openedPath);
        if (node is null) return;

        // 선택만 옮긴다 — 문서를 다시 열지 않는다. TreeView 의 재진입은 세터 첫 줄에서 멈춘다.
        MoveSelectionQuietly(() =>
        {
            node.IsExpanded = true;
            node.IsSelected = true;
        });
        _selected = node;
        Raise(nameof(Selected));
    }

    private bool _restoring;

    private void MoveSelectionQuietly(Action move)
    {
        _restoring = true;
        try { move(); }
        finally { _restoring = false; }
    }

    private static TreeNodeViewModel? FindByPath(IEnumerable<TreeNodeViewModel> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase)) return node;

            var hit = FindByPath(node.Children, path);
            if (hit is not null) return hit;
        }
        return null;
    }

    private string TargetFolder()
        => Selected is null
            ? _tree.Root
            : (Selected.IsFolder ? Selected.FullPath : Path.GetDirectoryName(Selected.FullPath)!);

    private bool TryGetValidName(string title, bool isFolder, out string name, out string parent)
    {
        name = "";
        parent = TargetFolder();

        var input = _dialogs.PromptText(title, "");
        if (input is null) return false;
        input = input.Trim();

        var directlyUnderRoot = string.Equals(PathRules.NormalizeFull(parent), _tree.Root, StringComparison.OrdinalIgnoreCase);
        var check = PathRules.CheckName(input, directlyUnderRoot);
        if (check != NameCheckResult.Ok)
        {
            _dialogs.Error("이름 오류", PathRules.MessageFor(check));
            return false;
        }

        var leaf = isFolder ? input : input + PathRules.DocumentExtension;

        // 메모리 트리가 아니라 디스크를 본다 (D-012)
        if (_tree.NameTaken(parent, leaf))
        {
            _dialogs.Error("이름 오류", "같은 이름이 이미 있습니다.");
            return false;
        }

        var full = Path.Combine(parent, leaf);
        if (PathRules.LongestDerivedLength(_tree.Root, full, "00000000-000000") > PathRules.MaxPathLength)
        {
            _dialogs.Error("이름 오류", "경로가 너무 깁니다. 상위 폴더 이름을 줄이거나 더 얕은 곳에 만들어주세요.");
            return false;
        }

        name = input;
        return true;
    }

    private void NewFolder()
    {
        if (IsKeyBusy) return;
        if (!TryGetValidName("새 폴더 이름", isFolder: true, out var name, out var parent)) return;

        Run("폴더 만들기", () => _tree.CreateFolder(parent, name));
    }

    private async Task NewDocumentAsync()
    {
        if (!_keys.HasKey)
        {
            _dialogs.Error("키 없음", "새 문서를 잠글 키가 없습니다. '키 입력'으로 키를 먼저 넣으세요.");
            return;
        }

        if (!TryGetValidName("새 문서 이름", isFolder: false, out var name, out var parent)) return;
        if (!ConfirmFirstDocumentKey()) return;

        // 만드는 즉시 유효한 빈 문서를 기록한다 (D-009). 예외는 Guarded가 받는다.
        await _store.CreateAsync(Path.Combine(parent, name + PathRules.DocumentExtension));
        await RefreshAsync();
    }

    private async Task RenameAsync()
    {
        if (Selected is null) return;

        var input = _dialogs.PromptText("새 이름", Selected.Name);
        if (input is null) return;
        input = input.Trim();

        var parent = Path.GetDirectoryName(Selected.FullPath)!;
        var directlyUnderRoot = string.Equals(PathRules.NormalizeFull(parent), _tree.Root, StringComparison.OrdinalIgnoreCase);

        // 문서는 원래 확장자를 뗀 줄기로 검사한다 — 「.txt」만 쓰면 줄기가 비어 이름이 없는 파일이 된다 (D-122)
        var name = Selected.IsFolder ? input : TreeService.StemOf(input, Path.GetExtension(Selected.FullPath));
        var check = PathRules.CheckName(name, directlyUnderRoot);
        if (check != NameCheckResult.Ok)
        {
            _dialogs.Error("이름 오류", PathRules.MessageFor(check));
            return;
        }

        var oldPath = Selected.FullPath;

        // 수정 중이라고 막지 않는다 — 자동 저장 앱에서 "저장 안 해서 못 바꿈"은 이해가 안 된다.
        // 먼저 저장하고 진행하되, 실패하면 이름을 바꾸지 않는다 (J-1).
        // 바꿔 두면 탭이 옛 경로를 쥔 채 남아 저장이 영영 실패한다.
        var affected = TabsAffectedBy(oldPath);
        if (!await PrepareAffectedTabsAsync(affected, "이름 변경")) return;

        string newPath;
        try
        {
            newPath = _tree.Rename(oldPath, input);
        }
        catch (Exception ex)
        {
            AppLog.Error("이름 변경", oldPath, ex);
            _dialogs.Error("이름 변경 실패", ErrorText.For(ex, "작업을 마치지 못했습니다."));
            return;
        }

        // 이름만 바뀐 것이지 문서가 사라진 게 아니다. 경로를 갱신하지 않고 닫아버리면
        // 사용자는 보던 내용이 없어졌다고 생각해 .trash를 뒤진다 (D-011).
        foreach (var tab in affected) FollowPath(tab, oldPath, newPath);

        // 이전 세대 탭은 원본을 따라가지 않는다 (J-2)
        foreach (var snapshot in SnapshotTabsFor(oldPath)) RemoveTab(snapshot);

        await RefreshAsync(newPath);
    }

    private async Task DeleteAsync()
    {
        if (Selected is null) return;
        if (!_dialogs.Confirm("삭제",
                $"'{Selected.Name}' 을(를) 휴지통으로 보낼까요?\n휴지통을 비우기 전까지는 되돌릴 수 있습니다.")) return;

        var deleting = Selected.FullPath;

        // 지우는 것이므로 저장하지 않는다. 다만 예약은 반드시 끈다 —
        // 살아 있으면 방금 휴지통으로 보낸 문서를 옛 자리에 되살린다 (F-4).
        var affected = TabsAffectedBy(deleting);
        foreach (var tab in affected) tab.CancelPendingSave();

        try
        {
            _tree.MoveToTrash(deleting, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }
        catch (Exception ex)
        {
            AppLog.Error("삭제", deleting, ex);
            _dialogs.Error("삭제 실패", ErrorText.For(ex, "작업을 마치지 못했습니다."));
            return;
        }

        foreach (var tab in affected) RemoveTab(tab);
        foreach (var snapshot in SnapshotTabsFor(deleting)) RemoveTab(snapshot);

        await RefreshAsync();
    }

    private void EmptyTrash()
    {
        if (IsKeyBusy) return;

        // 세는 것도 디스크를 읽는다. 여기서 새는 예외는 명령 밖으로 나가 앱을 종료시킨다 —
        // .trash 가 링크면 LinkedPathException, 링크 대상이 사라졌으면 DirectoryNotFoundException [실측].
        int count;
        try
        {
            count = _tree.CountTrashItems();
        }
        catch (Exception ex)
        {
            AppLog.Error("휴지통 비우기", null, ex);
            _dialogs.Error("휴지통 비우기 실패", ErrorText.For(ex, "휴지통을 읽지 못했습니다."));
            return;
        }
        if (count == 0)
        {
            _dialogs.Error("휴지통", "휴지통이 비어 있습니다.");
            return;
        }

        // 앱에서 되돌릴 수 없는 유일한 동작이다
        if (!_dialogs.Confirm("휴지통 비우기", $"{count}개 문서를 영구 삭제합니다. 되돌릴 수 없습니다.\n계속할까요?")) return;

        Run("휴지통 비우기", () => _tree.EmptyTrash());
    }

    /// <summary>
    /// design.md 8.7 — .bak은 숨김이고 트리에도 없어서, 접근 경로를 주지 않으면
    /// D-004가 "유일한 복구 수단"이라 부른 것을 앱에서 쓸 수 없다.
    /// </summary>
    private async Task OpenBackupAsync()
    {
        if (Selected is null || Selected.IsFolder) return;

        // 평문에는 이력이 없다. "아직 연 적이 없습니다"는 .txt 에 영원히 거짓이라,
        // 사용자가 한 번 더 열어보면 생길 거라고 오해한다.
        if (Selected.IsPlainText)
        {
            // 평문 사본을 늘리지 않는다 (D-117)
            _dialogs.Error("되돌릴 지점 없음", "평문(.txt)은 사본을 남기지 않아 되돌릴 지점이 없습니다.");
            return;
        }

        var snapshot = _store.SnapshotPathIfExists(Selected.FullPath);
        if (snapshot is null)
        {
            _dialogs.Error("되돌릴 지점 없음", "이 문서를 아직 연 적이 없어 비교할 상태가 없습니다.");
            return;
        }

        // 스냅샷도 탭이다 (J-2). 편집기를 점유하면 원본으로 돌아갈 방법이 없다 —
        // 나란히 두어야 필요한 부분만 복사해 붙여넣을 수 있다.
        if (FindTab(snapshot) is { } opened)
        {
            ActiveTab = opened;
            return;
        }

        var editor = _editorFactory.Create();
        await editor.LoadOpenSnapshotAsync(snapshot, Selected.Name);
        Tabs.Add(editor);
        ActiveTab = editor;
    }

    /// <summary>
    /// 우클릭한 자리를 Windows 탐색기로 보여준다.
    /// 폴더·금고 루트·빈 곳은 그 폴더 <b>안</b>을 열고, 파일(.tbx·.txt)은 상위 폴더를 열어 그 파일을 선택한다.
    ///
    /// 금고 루트를 "상위에서 선택"으로 열면 금고의 부모 폴더, 즉 <b>금고 밖 형제 폴더들</b>이 보이는 창이 된다.
    /// 그래서 루트는 반드시 안으로 연다.
    /// </summary>
    private async Task OpenInExplorerAsync()
    {
        const string title = "탐색기에서 열기";

        var node = Selected;
        var root = _tree.Root;

        // 빈 곳을 우클릭하면 Selected 가 null 이다 — 다른 명령과 같이 금고 최상위로 본다 (TargetFolder 관례)
        var target = PathRules.NormalizeFull(node?.FullPath ?? root);
        var asFolder = node is null || node.IsFolder;

        // 트리 불변식에 기대지 않는다. F5 를 누르기 전에 디스크가 바뀐 노드가 남아 있을 수 있다 (D-008).
        if (!PathRules.IsInsideRoot(root, target) || PathRules.IsReservedArea(root, target))
        {
            _dialogs.Error(title, "금고 밖이나 앱이 쓰는 영역은 열 수 없습니다.");
            return;
        }

        var opened = asFolder
            ? await _explorer.OpenFolderAsync(target)
            : await _explorer.RevealAsync(target);

        // 찾지 못하면 아무것도 열지 않고 알린다. 대신 다른 곳(상위 폴더 등)을 열지 않는다 —
        // 요청한 것을 못 찾고 조용히 엉뚱한 곳을 여는 것이 explorer.exe 방식을 버린 이유다.
        // 문구에 경로를 싣지 않는다 (PROHIBITED-CUSTOM-04 취지).
        if (!opened)
        {
            _dialogs.Error(title,
                "이 항목을 찾을 수 없습니다.\n\n밖에서 이름이 바뀌었거나 지워졌을 수 있습니다. 도구 모음 [새로고침](기본 F5)으로 새로고침하세요.");
        }
    }

    private async Task ChangeRootAsync()
    {
        if (!await SaveAllTabsAsync("금고 폴더 변경")) return;

        var picked = _dialogs.PickFolder(_tree.Root, "금고 폴더 선택");
        if (picked is null) return;

        await ApplyRootAsync(picked);
    }

    private void Run(string action, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            AppLog.Error(action, null, ex);
            _dialogs.Error($"{action} 실패", ErrorText.For(ex, "작업을 마치지 못했습니다."));
            return;
        }

        Guarded("새로고침", RefreshAsync);   // 새로고침 예외도 같은 방어를 탄다
    }

    // ── 키 ───────────────────────────────────────────────────────────────────
    // 앱은 한 번에 키 하나를 쓴다. 문서는 만들 때의 키로 잠긴 채 그대로 있고,
    // 키를 바꿔 끼우면 열리는 문서가 달라진다 (D-047 · D-048).

    private const string FirstKeyNotice =
        "문서를 잠글 키를 정하세요. 다른 PC 에서도 이 키만 있으면 열립니다.\n\n"
        + "키를 잊으면 누구도 이 문서들을 열 수 없습니다. 복구 수단이 없습니다.\n"
        + "8자 이상, 영문·숫자·기호로 정하세요.";

    private int _busyDepth;

    /// <summary>
    /// 키를 판정·전환·잠그는 중, 그리고 금고 폴더를 바꾼 뒤 다시 판정하는 중.
    /// 이 동안은 열기·만들기·옮기기·삭제·검색을 받지 않는다 — 옛 세대로 탭이 열린 뒤 세대가 바뀌면
    /// 그 탭의 저장은 영영 거부된다.
    /// 흐름이 겹쳐도(금고 변경 → 키 입력) 바깥이 끝날 때까지 유지되도록 깊이로 센다.
    /// </summary>
    public bool IsKeyBusy => _busyDepth > 0;

    private BusyScope BeginBusy()
    {
        if (_busyDepth++ == 0) RaiseBusyChanged();
        return new BusyScope(this);
    }

    private void EndBusy()
    {
        if (--_busyDepth == 0) RaiseBusyChanged();
    }

    private void RaiseBusyChanged()
    {
        Raise(nameof(IsKeyBusy));
        Raise(nameof(KeyStatusText));
        Raise(nameof(ChangeKeyLabel));
        RaiseKeyPresence();
    }

    /// 트레이 툴팁 · 메뉴(「잠그기」/「키 입력…」)가 따라온다.
    private void RaiseKeyPresence()
    {
        Raise(nameof(HasKey));
        Raise(nameof(TrayToolTip));
    }

    private sealed class BusyScope(ShellViewModel owner) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            owner.EndBusy();
        }
    }

    private int _matchCount;
    private int _documentCount;

    public string KeyStatusText
        => IsKeyBusy ? "키 확인 중…"
         : _keys.HasKey ? $"키: 사용 중 · 맞는 문서 {_matchCount}/{_documentCount}"
         : "키 없음";

    public bool HasKey => _keys.HasKey;

    /// <summary>
    /// 트레이 아이콘 툴팁. 문서 이름 · 경로 · 개수를 넣지 않는다 — Windows 가 툴팁을 사용자 레지스트리
    /// (NotifyIconSettings 의 InitialTooltip)에 남긴다 [실측] (D-102 · PROHIBITED-CUSTOM-04).
    /// </summary>
    public string TrayToolTip
        => IsKeyBusy ? "TextBean - 키 확인 중"
         : _keys.HasKey ? "TextBean - 키 사용 중"
         : "TextBean - 키 없음";

    /// 앱 대화상자가 떠 있는가. 트레이 메뉴처럼 모달이 끄지 못하는 곳에서 View 가 본다 (D-104).
    public bool IsDialogShowing => _dialogs.IsShowing;

    /// Windows 종료 · 절전 대기 동안 대화상자를 띄우지 않는다 — 대기가 그 창에 묶이지 않게 (D-101 · D-104).
    public IDisposable SuppressDialogs() => _dialogs.Suppress();

    /// <summary>
    /// 키가 바뀌었거나 잠갔다. 검색 창이 결과·상태를 비운다 — 창을 다시 쓰므로 옛 키의 결과가 남는다.
    /// 인자가 true 면 잠그기다(검색어도 비운다 — 검색어 자체가 비밀값일 수 있다).
    /// </summary>
    public event EventHandler<bool>? KeyContextReset;

    /// 창 닫기를 막을 때 View 가 부른다.
    public void WarnBusy() => _dialogs.Error("잠시 기다려 주세요", "키를 확인하는 중입니다. 끝난 뒤 다시 시도해주세요.");

    /// <summary>
    /// 앱을 켠 뒤 App 이 한 번 부른다. 금고 상태에 따라 순서가 정해져 있다 (plan §2-7):
    /// 새 방식 문서가 하나도 없으면 처음 키 정하기 · 있으면 키 입력.
    /// </summary>
    public Task StartAsync()
    {
        Guarded("키 입력", () => EnsureKeyForVaultAsync(afterRootChange: false));
        return Pending;
    }

    private async Task EnsureKeyForVaultAsync(bool afterRootChange)
    {
        var headers = await ReadVaultHeadersAsync();

        // 금고를 바꿨으면 지금 키로 새 금고를 다시 판정해 새 세대로 세운다. 옛 금고의 "맞는 문서" 판정이 남으면
        // 새 금고에서 첫 문서 재입력(오타 방지)이 빠진다. 탭은 ApplyRootAsync 가 이미 전부 닫았다.
        if (afterRootChange && _keys.HasKey)
        {
            var again = await Task.Run(() => _keys.EvaluateCurrent(headers));
            if (again is not null) _keys.Activate(again);
            await RefreshKeyStatesAsync();
        }

        // 옛 방식(DPAPI) 문서는 이 버전에서 열지 않는다(D-073). 트리에 흐리게 남고, 열면 이유를 알린다.
        if (_keys.HasKey) return;

        if (headers.Any(h => h?.Kind == DocumentHeaderKind.Valid))
            await EnterKeyAsync("키 입력", "이 금고의 문서를 연 키를 넣으세요.");
        else
            await SetFirstKeyAsync("처음 키 정하기");
    }

    private async Task ChangeKeyAsync()
    {
        // 키도 없고 새 방식 문서도 없으면 이것이 처음 정하는 키다 — 두 번 입력 · 규칙 · 고지를 거친다
        if (!_keys.HasKey && !(await ReadVaultHeadersAsync()).Any(h => h?.Kind == DocumentHeaderKind.Valid))
        {
            await SetFirstKeyAsync("처음 키 정하기");
            return;
        }

        // 잠근 뒤에는 바꾸는 것이 아니라 다시 넣는 것이다 — 버튼 이름(ChangeKeyLabel)과 같은 말을 쓴다
        if (_keys.HasKey)
            await EnterKeyAsync("키 변경", "쓸 키를 넣으세요. 키를 바꾸면 열린 문서를 저장하고 닫습니다.");
        else
            await EnterKeyAsync("키 입력", "이 금고의 문서를 연 키를 넣으세요.");
    }

    /// 버튼 이름. 잠근 뒤 "키 변경"을 눌러야 풀리면, 무엇을 눌러야 하는지 헷갈린다 (사용자 확인 2026-09-30).
    public string ChangeKeyLabel => _keys.HasKey ? "키 변경" : "키 입력";

    private async Task<bool> EnterKeyAsync(string title, string message)
    {
        while (true)
        {
            var entry = _dialogs.PromptKey(title, message, confirm: false);
            if (entry is null) return false;

            // 새 키와 같은 규칙이다. 규칙을 못 넘는 키로 잠긴 문서는 없다 — 받아 주면 아무것도 안 열리는 키가 켜진다
            var rule = KeyRules.CheckNew(KeyRules.Normalize(entry.Key));
            if (rule != KeyRuleResult.Ok)
            {
                _dialogs.Error(title, KeyRules.MessageForEntered(rule));
                continue;
            }

            return await SwitchKeyAsync(entry.Key, title, firstKey: false);
        }
    }

    /// 두 번 입력 · 새 키 규칙 · "잊으면 복구 불가" 고지.
    private async Task<bool> SetFirstKeyAsync(string title)
    {
        var key = PromptNewKey(title, FirstKeyNotice);
        return key is not null && await SwitchKeyAsync(key, title, firstKey: true);
    }

    private string? PromptNewKey(string title, string message)
    {
        while (true)
        {
            var entry = _dialogs.PromptKey(title, message, confirm: true);
            if (entry is null) return null;

            var normalized = KeyRules.Normalize(entry.Key);
            var rule = KeyRules.CheckNew(normalized);
            if (rule != KeyRuleResult.Ok)
            {
                _dialogs.Error(title, KeyRules.MessageFor(rule));
                continue;
            }

            if (!string.Equals(normalized, KeyRules.Normalize(entry.Confirmation ?? ""), StringComparison.Ordinal))
            {
                _dialogs.Error(title, "두 번 입력한 키가 다릅니다. 다시 입력하세요.");
                continue;
            }

            return entry.Key;
        }
    }

    /// <summary>
    /// 키 전환 절차 (plan §2-7). 순서가 핵심이다:
    /// 판정(지금 키·탭은 그대로) → 확인 → 열린 탭을 옛 키로 저장하고 닫기(2단 재확인) → 탭 0개 확인 뒤 await 없이 교체.
    /// 금고 폴더 변경의 흐름을 복제하지 않는다 — 그쪽은 저장과 해제 사이에 await 가 끼어 그 사이 입력이 사라진다.
    /// </summary>
    private async Task<bool> SwitchKeyAsync(string key, string title, bool firstKey)
    {
        var busy = BeginBusy();
        KeyEvaluation? evaluation = null;
        try
        {
            CancelSearch();
            var headers = await ReadVaultHeadersAsync();
            evaluation = await Task.Run(() => _keys.Evaluate(key, headers));

            // 바꿔 끼울 때와, 처음 넣는 키가 금고의 어느 문서와도 안 맞을 때(오타일 수 있다) 확인한다
            var ask = !firstKey && (_keys.HasKey || (evaluation.MatchCount == 0 && evaluation.Total > 0));
            if (ask && !_dialogs.Confirm(title, DescribeEvaluation(evaluation))) return false;

            if (!await CloseKeyBoundTabsAsync(title)) return false;

            _keys.Activate(evaluation);
            evaluation = null;

            LastSearch = null;
            KeyContextReset?.Invoke(this, false);
            await RefreshKeyStatesAsync();
            return true;
        }
        finally
        {
            if (evaluation is not null) _keys.Discard(evaluation);      // 오타 키일 수 있다 — 만든 키를 지운다
            busy.Dispose();
        }
    }

    private async Task LockAsync()
    {
        // 잠그기는 「지금 멈춤」이다 — 키가 없어도(.txt 탭만 열려 있어도) 마이크는 끈다 (D-186)
        _dictation?.Cancel();

        if (!_keys.HasKey) return;

        var busy = BeginBusy();
        try
        {
            CancelSearch();
            if (!await CloseKeyBoundTabsAsync("잠그기")) return;

            await ClearKeyAfterTabsClosedAsync();
        }
        finally
        {
            busy.Dispose();
        }
    }

    /// <summary>
    /// 절전 · 최대 절전에 들어갈 때 부른다 — 최대 절전은 메모리를 디스크(hiberfil)에 쓴다 [문서] (D-100).
    /// 대화상자 없이 키에 묶인 탭을 저장하고 닫은 뒤 키를 지운다. 못 하면 잠그지 않는다 — 물을 수 없는 자리에서
    /// 저장 못 한 편집 내용을 버리지 않는다(fail-open). 탭이 다 저장된 상태면 첫 await 전에 키가 「없음」이 된다 —
    /// Windows 가 기다려 주는 시간이 약 2초다.
    /// </summary>
    public async Task<bool> LockQuietlyAsync()
    {
        // 잠그기는 「지금 멈춤」이다 — 키가 없어도(.txt 탭만 열려 있어도) 마이크는 끈다 (D-186)
        _dictation?.Cancel();

        if (!_keys.HasKey) return true;

        // 키 전환과 겹치면 같은 탭을 두 길이 저장하고 닫는다
        if (IsKeyBusy)
        {
            AppLog.Warn("suspend-lock-skipped", null, null);
            return false;
        }

        using var busy = BeginBusy();
        CancelSearch();

        // 조용히 닫기는 전부 닫았을 때만 true 다 — 바쁨 표시가 그 사이 새 탭이 열리는 것을 막는다.
        // 여러 탭을 닫는 중이면 CloseManyAsync 가 겹치지 않게 false 를 돌려준다. 키에 묶인 탭이 없으면 닫을 것이 없어 바로 잠근다
        var targets = Tabs.Where(t => !t.IsPlainText).ToList();
        if (targets.Count > 0 && !await CloseManyAsync(targets, "절전 잠그기", quiet: true))
        {
            AppLog.Warn("suspend-lock-skipped", null, null);
            return false;
        }

        await ClearKeyAfterTabsClosedAsync();
        return true;
    }

    /// 잠그기 공통 뒷부분. 키에 묶인 탭이 다 닫힌 뒤에 부른다.
    private async Task ClearKeyAfterTabsClosedAsync()
    {
        // 키가 먼저다. 클립보드 정리는 다른 프로그램이 클립보드를 쥐고 있으면 재시도하다 알림 창까지 띄울 수 있다 —
        // 절전 진입 때 그 사이 Windows 가 기다려 주는 시간(약 2초)이 지나면 키가 지워지지 않은 채 잠든다 (비판 검토 R1)
        _keys.Clear();
        _clipboard.ClearIfOurs();          // 자리를 뜬다는 뜻이다 — 우리가 넣은 값은 지운다

        // 트레이 메뉴가 「키 입력…」으로 바로 바뀐다 — 아래 트리 상태 갱신(파일 읽기)을 기다리지 않는다
        RaiseKeyPresence();

        LastSearch = null;
        KeyContextReset?.Invoke(this, true);
        await RefreshKeyStatesAsync();
    }

    /// <summary>
    /// 키에 묶인 탭(.tbx · 스냅샷 · 잠긴 탭)을 옛 키로 저장하고 닫는다. 평문(.txt) 탭은 둔다 — 키와 무관하다.
    /// 하나라도 남으면 false. 남은 탭이 새 키 세대에서 저장하면 문서가 다시 잠기므로 전환을 멈춘다.
    /// </summary>
    private async Task<bool> CloseKeyBoundTabsAsync(string action)
    {
        var targets = Tabs.Where(t => !t.IsPlainText).ToList();
        if (targets.Count > 0) await CloseManyAsync(targets, action);

        return Tabs.All(t => t.IsPlainText);
    }

    private static string DescribeEvaluation(KeyEvaluation evaluation)
    {
        var parts = new List<string>();
        if (evaluation.OtherKeyCount > 0) parts.Add($"다른 키 {evaluation.OtherKeyCount}");
        if (evaluation.LegacyCount > 0) parts.Add($"옛 방식 {evaluation.LegacyCount}");
        if (evaluation.CorruptedCount > 0) parts.Add($"손상 {evaluation.CorruptedCount}");
        if (evaluation.UnreadableCount > 0) parts.Add($"읽지 못함 {evaluation.UnreadableCount}");

        var detail = parts.Count == 0 ? "" : $" ({string.Join(" · ", parts)})";
        var warning = evaluation.MatchCount == 0 && evaluation.Total > 0
            ? "\n\n이 키가 맞는 문서가 하나도 없습니다. 오타일 수 있습니다."
            : "";

        // "열리는"이 아니라 "맞는" — 키 확인값만 본 수라 본문만 손상된 문서가 섞일 수 있다 [실측]
        return $"이 키가 맞는 문서 {evaluation.MatchCount}개 / 전체 {evaluation.Total}개{detail}{warning}\n\n"
               + "이 키를 쓸까요? 열린 문서는 저장하고 닫습니다.";
    }

    /// <summary>
    /// 이 키로 만드는 첫 문서면 한 번 더 입력받는다. 앱은 오타와 새 키를 구분할 수 없다 —
    /// 오타 키로 만든 문서는 나중에 그 오타를 기억하지 못하면 영영 못 연다.
    /// 확인 창은 습관적으로 눌려 막지 못한다(D-006) — 다시 치게 한다.
    /// </summary>
    private bool ConfirmFirstDocumentKey()
    {
        using (var lease = _keys.Acquire())
        {
            if (lease is null || lease.HasMatchedKey) return lease is not null;
        }

        var entry = _dialogs.PromptKey("키 확인",
            "지금 키로 만드는 첫 문서입니다. 오타가 아닌지 키를 한 번 더 입력하세요.", confirm: false);
        if (entry is null) return false;

        if (_keys.Matches(entry.Key)) return true;

        _dialogs.Error("키 확인", "지금 쓰는 키와 다릅니다. 문서를 만들지 않았습니다.");
        return false;
    }

    /// 트리에 보이는 금고 문서(.tbx)의 헤더. 예약 영역(.history · .trash)은 트리에 없어 들어가지 않는다.
    private Task<IReadOnlyList<DocumentHeader?>> ReadVaultHeadersAsync()
        => _store.ReadHeadersAsync(EncryptedNodes().Select(n => n.FullPath).ToList());

    private IEnumerable<TreeNodeViewModel> EncryptedNodes()
    {
        var pending = new Stack<TreeNodeViewModel>(Roots);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!node.IsFolder && !node.IsPlainText) yield return node;
            foreach (var child in node.Children) pending.Push(child);
        }
    }

    /// 트리의 문서마다 지금 키로 열리는지 표시한다. 파일 하나가 실패해도 그 노드만 "읽을 수 없음"이다.
    private async Task RefreshKeyStatesAsync()
    {
        var nodes = EncryptedNodes().ToList();
        var states = await _store.ClassifyAsync(nodes.Select(n => n.FullPath).ToList());

        for (var i = 0; i < nodes.Count; i++) nodes[i].KeyState = states[i];

        _matchCount = states.Count(s => s == DocumentKeyState.Matches);
        _documentCount = nodes.Count;
        Raise(nameof(KeyStatusText));
        Raise(nameof(ChangeKeyLabel));
    }

    /// <summary>
    /// 종료 시 App 이 부른다. 편집기는 조립 루트가 아니라 여기서 만들어지므로
    /// 해제 책임도 여기에 있다 — App._disposables 에 편집기를 직접 넣으면
    /// Remove 가 없어 닫힌 탭이 목록에 남고 평문이 종료까지 산다.
    /// </summary>
    public void Dispose()
    {
        _dictation?.Cancel();
        CloseAllTabs();
    }
}
