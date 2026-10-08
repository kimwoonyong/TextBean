using System.IO;
using TextBean.Models;
using TextBean.Services;
using TextBean.Services.Interfaces;

namespace TextBean.ViewModels;

public sealed class EditorViewModel : ObservableObject, IDisposable
{
    private readonly IDocumentStore _store;
    private readonly IClipboardService _clipboard;
    private readonly IDialogService _dialogs;
    private readonly IAutoSaveTimer _autoSave;

    private string _text = "";

    // 불러온(또는 마지막으로 저장한) 서식 문서. 화면이 붙어 있지 않을 때의 저장 본문이다 (D-123)
    private byte[] _rich = [];

    // 편집마다 +1. 저장 중 입력 보호는 글자가 아니라 이 값으로 본다 — 서식만 바꾼 입력은 글자로 보이지 않는다 (D-129)
    private int _editVersion;
    private bool _isDirty;
    private bool _isReadOnly;
    private bool _lastSaveFailed;
    private string? _lockReason;
    private string? _currentPath;
    private bool _loadFailed;
    private bool _disposed;
    private bool _isActive;
    private string _snapshotOwnerName = "";
    private bool _isPlainText;
    private string? _encodingLabel;
    private bool _encodingIsGuess;

    // 평문을 읽은 형식 — 저장할 때 이대로 다시 쓴다 (D-118)
    private PlainTextFormat? _plainFormat;

    // 평문 인코딩에 못 담는 글자로 실패했을 때의 이유. 자동 저장은 창을 띄우지 않아 상태 줄이 알린다 (D-119)
    private string? _saveFailReason;
    private DocumentKeyBinding? _binding;
    private DocumentReadStatus _loadStatus = DocumentReadStatus.Ok;

    /// <summary>
    /// 본문에 올릴 수 있는 최대 글자 수. 읽는 것은 싸지만(15MB 바이트 읽기 + 디코드 18ms)
    /// TextBox 에 올리는 것은 비싸다 — 1MB 529ms, 5MB 2,041ms, 20MB 6,099ms [실측].
    /// .tbx 는 앱이 만든 메모라 작지만 .txt 는 사용자가 떨군 임의 파일이라 상한이 없다.
    /// 상한을 두지 않으면 "클릭하면 볼 수 있다"가 "클릭하면 6초 멈춘다"가 된다.
    /// 상한은 표시에만 건다 — 읽기 계층에 걸면 큰 파일이 검색에서 "읽지 못한 문서"로 세어진다.
    /// </summary>
    private const int MaxDisplayChars = 1_000_000;

    public EditorViewModel(IDocumentStore store, IClipboardService clipboard, IDialogService dialogs,
                           IAutoSaveTimer autoSave)
    {
        _store = store;
        _clipboard = clipboard;
        _dialogs = dialogs;
        _autoSave = autoSave;

        // 자동 저장 실패를 매번 대화상자로 띄우면 1.5초마다 팝업이 뜬다.
        // 조용히 실패하지도 않는다 — 상태표시줄과 로그에 남긴다.
        // 람다가 아니라 메서드로 구독한다 — 해제할 때 떼어내야 한다.
        _autoSave.Elapsed += OnAutoSaveElapsed;
    }

    private void OnAutoSaveElapsed(object? sender, EventArgs e)
    {
        if (_disposed) return;
        _ = SaveCoreAsync(notifyUser: false);
    }

    public string? CurrentPath
    {
        get => _currentPath;
        private set
        {
            if (!Set(ref _currentPath, value)) return;
            Raise(nameof(DocumentName));
            Raise(nameof(TabTitle));
            Raise(nameof(IsPlainTextFile));
            Raise(nameof(ShowFormatBar));
            Raise(nameof(HasDocument));
            Raise(nameof(CanCopy));
            Raise(nameof(StatusText));
            Raise(nameof(SaveState));
        }
    }

    /// <summary>
    /// 표시 이름. 금고 문서는 확장자를 감추고 평문은 드러낸다.
    /// 비대칭인 이유: 확장자를 감추는 규칙은 금고에 한 종류만 있을 때 성립했다.
    /// 둘이 섞이면 '메모.tbx' 와 '메모.txt' 가 트리·탭·검색 결과에서 모두 '메모' 하나로 겹치고,
    /// 아이콘이 없는 탭과 검색 결과에서는 구분할 방법이 아예 없어진다 [실측].
    /// 판정은 여기서 한다 — XAML 에서 경로 문자열을 다루지 않는다 (PROHIBITED-UI-03).
    /// </summary>
    public string DocumentName => CurrentPath is null
        ? ""
        : PathRules.IsPlainText(CurrentPath)
            ? Path.GetFileName(CurrentPath)
            : Path.GetFileNameWithoutExtension(CurrentPath);

    public bool HasDocument => CurrentPath is not null;

    /// <summary>
    /// 지금 보고 있는 것이 암호화되지 않은 참고 파일인가(읽는 데 성공했는가). 배너 톤과 탭 아이콘의 흐림이 이 값을 본다 —
    /// 평문의 읽기 전용은 고칠 수 없는 상태가 아니라 파일 종류라 흐리지 않는다.
    /// 읽는 데 실패한 .txt 는 false 다. 그쪽은 정상이 아니라 오류이므로 오류 배너가 맞고, 탭 아이콘도 흐리다.
    /// </summary>
    public bool IsPlainText => _isPlainText;

    /// <summary>
    /// 파일 종류가 평문(.txt)인가 — 읽었는지와 상관없이 경로로 본다. 탭 아이콘의 그림이 이 값을 본다.
    /// 트리도 경로로 그림을 고르므로, 읽지 못한 .txt 가 트리에서는 평문 그림인데 탭에서는 "다른 키로 잠긴 암호 문서" 그림(흐린 자물쇠 문서)이었다
    /// [실측 — 3차 검토, D-091]. 위의 <see cref="IsPlainText"/>(읽는 데 성공한 평문)는 배너 톤과 키 바꿀 때 닫을 탭 판정이 쓴다 — 뜻을 바꾸지 않는다.
    /// </summary>
    public bool IsPlainTextFile => CurrentPath is { } path && PathRules.IsPlainText(path);

    /// 서식 도구 모음 — .tbx 를 고칠 수 있을 때만 (D-127). .txt 는 서식을 담지 못한다.
    public bool ShowFormatBar => HasDocument && !IsPlainTextFile && !IsReadOnly;

    /// 무엇으로 읽었는지. 평문이 아니면 null.
    public string? EncodingLabel => _encodingLabel;

    /// 이 편집기가 이력 스냅샷을 보고 있는가.
    public bool IsSnapshot { get; private set; }

    /// <summary>
    /// 문서를 읽지 못해 잠긴 상태인가. 이때 <see cref="Text"/> 는 빈 문자열이라,
    /// 그대로 검색하면 "뒤져봤고 없었다"는 거짓 답이 된다 — 읽기 전용(스냅샷)과 구분해야 한다.
    /// </summary>
    public bool LoadFailed => _loadFailed;

    /// <summary>
    /// 왜 읽지 못했는가. 검색이 "다른 키 문서"와 "읽지 못한 문서"를 가르는 데 쓴다 —
    /// LoadFailed 만 보면 다른 키 문서가 "읽지 못함"으로 세어져 범위마다 문구가 달라진다.
    /// </summary>
    public DocumentReadStatus LoadStatus => _loadStatus;

    /// 열 때 받은 키 결속. 저장은 이 키로만 한다 (다른 키로 다시 잠그지 않는다).
    public DocumentKeyBinding? KeyBinding => _binding;

    /// <summary>
    /// 마지막 저장이 키 때문에 거부됐다 — 키가 바뀌었거나 파일이 다른 키 문서로 바뀌었다.
    /// 다시 해도 영영 실패하므로, 셸이 "편집 내용을 버리고 닫기"를 따로 묻는다.
    /// 안 그러면 저장 실패 탭은 닫히지 않고 종료도 막혀(D-011) 앱을 강제로 끄는 수밖에 없다.
    /// </summary>
    public bool SaveBlockedByKey { get; private set; }

    /// 탭 머리글. 스냅샷은 파일명이 전부 'opened' 라 어느 문서 것인지 알 수 없다 [실측].
    public string TabTitle => IsSnapshot ? $"{_snapshotOwnerName} (열었을 때)" : DocumentName;

    /// <summary>
    /// 탭 마커가 바인딩한다. "수정됨"만으로는 1.5초 깜빡이는 장식이 되므로
    /// 지속되는 위험 상태인 Failed 를 따로 가른다.
    /// </summary>
    public SaveState SaveState =>
        IsReadOnly      ? SaveState.ReadOnly :
        _lastSaveFailed ? SaveState.Failed :
        IsDirty         ? SaveState.Saving :
                          SaveState.Saved;

    /// 복사를 막는 기준은 "읽기 전용"이 아니라 "읽지 못했다"이다.
    /// 이전 세대는 읽기 전용이면서도 복사하라고 여는 것이므로 둘을 갈라야 한다 (8.7).
    public bool CanCopy => HasDocument && !_loadFailed && Text.Length > 0;

    /// 저장 여부가 화면에 드러나지 않으면 사용자는 "저장 버튼이 반응이 없다"고 느낀다.
    public string StatusText
    {
        get
        {
            if (!HasDocument) return "문서를 선택하세요";

            // 평문은 무엇으로 읽었는지(추정인지)를 늘 앞에 붙인다 — 인코딩을 잘못 고르면 깨진 글자가 조용히 그려진다 (D-021)
            if (IsPlainText)
            {
                var encoding = _encodingLabel + (_encodingIsGuess ? "(추정)" : "");
                if (_lastSaveFailed) return $"{encoding} · ⚠ 저장 실패 — {_saveFailReason ?? "내용이 아직 저장되지 않았습니다"}";
                return $"{encoding} · {(IsDirty ? "저장 중…" : "저장됨")}";
            }
            if (IsReadOnly) return "읽기 전용 — 저장할 수 없습니다";
            if (_lastSaveFailed && SaveBlockedByKey) return "⚠ 저장할 수 없음 — 이 파일은 지금 키로 잠긴 문서가 아닙니다";
            if (_lastSaveFailed) return "⚠ 저장 실패 — 내용이 아직 저장되지 않았습니다";
            return IsDirty ? "저장 중…" : "저장됨";
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (!Set(ref _text, value)) return;

            _editVersion++;
            IsDirty = true;
            Raise(nameof(CanCopy));

            // 본문이 바뀌면 일치 자리도 바뀐다. 다시 세지 않으면 강조가 엉뚱한 줄에 남아
            // "이 값이 일치했다"고 잘못 알려준다 — 비밀 보관함에서는 단순 표시 오류가 아니다.
            RecomputeMatches();

            // 입력이 멈추면 자동으로 저장한다 (D-015). 잠긴 문서는 저장 경로가 막혀 있다.
            if (!IsReadOnly && HasDocument) _autoSave.Restart();
        }
    }

    // ── 서식 본문 (.tbx — RichTextBox, D-125) ─────────────────────────────────
    // 서식 문서는 화면이 들고 있다(FlowDocument 는 바인딩할 수 없다). 이 편집기는 검색용 글자와 편집 사실만 받는다.

    /// 불러온 서식 문서(XamlPackage). 비면 서식 없음 — 화면이 Text 를 문단으로 나눠 보인다 (D-123).
    public byte[] Rich => _rich;

    /// 화면이 등록한다: 지금 본문을 [검색용 글자 + 서식 바이트]로 뽑는다. UI 스레드에서 부른다.
    /// 없으면(화면이 아직 없거나 시험) 불러온 본문을 그대로 저장한다.
    public Func<DocumentBody>? CaptureBody { get; set; }

    /// 화면이 등록한다: 문서 전체를 서식째 복사할 내용 (「전체 복사」).
    public Func<ClipboardPayload>? CaptureAllForCopy { get; set; }

    /// <summary>
    /// 테마를 바꾸기 직전 (D-165). 지금 본문을 서식 바이트로 거둬 둔다 — 바꾸면 화면이 본문을 새로 만들고 [실측] 이 바이트로 다시 연다.
    /// 저장하지 않고 「고쳐짐」 · 편집 버전도 건드리지 않는다 — 고친 것은 그대로 자동 저장을 기다린다.
    /// 화면이 없거나(시험 · .txt) 못 읽은 문서는 하지 않는다 — 못 읽은 문서의 빈 본문이 원본 바이트를 덮으면 안 된다 (D-005).
    /// </summary>
    public void StashBody()
    {
        if (_loadFailed || CaptureBody is not { } capture) return;
        _rich = capture().Rich;
    }

    /// 테마를 바꾼 직후 (D-165). 화면이 거둔 바이트로 본문을 다시 연다 — 새 테마 색으로. 새로 만들어진 본문은 묶이며 스스로도 연다(두 번 열어도 같다).
    public void ReloadBody()
    {
        if (_loadFailed || _isPlainText || !HasDocument) return;
        Raise(nameof(Rich));
    }

    /// 화면이 알린다: 글자든 서식이든 본문이 바뀌었다. 글자는 SyncText 로 따로 온다.
    public void MarkEdited()
    {
        if (IsReadOnly || !HasDocument) return;

        _editVersion++;
        IsDirty = true;
        _autoSave.Restart();
    }

    /// <summary>
    /// 화면이 알린다: 서식 문서에서 뽑은 글자 (D-128). 수정됨으로 치지 않는다 — 수정은 MarkEdited 가 알린다.
    /// 불러온 직후에도 부른다. 문단 줄바꿈 표기가 저장된 글자와 다를 수 있어, 안 맞추면 찾기 위치가 어긋난다.
    /// </summary>
    public void SyncText(string text)
    {
        if (!Set(ref _text, text, nameof(Text))) return;

        Raise(nameof(CanCopy));
        RecomputeMatches();
    }

    /// <summary>
    /// 화면이 알린다: 서식 바이트를 문서로 만들지 못했다. 손상으로 잠근다 —
    /// 빈 문서로 두고 저장을 열어 두면 자동 저장이 원본을 덮는다 (D-005).
    /// </summary>
    public void RichLoadFailed(Exception ex)
    {
        AppLog.Warn("rich-load", CurrentPath, ex);       // 경로와 예외 형식까지만 (PROHIBITED-CUSTOM-04)

        _autoSave.Stop();
        _text = "";
        _rich = [];
        _loadFailed = true;
        _loadStatus = DocumentReadStatus.Corrupted;
        IsReadOnly = true;
        IsDirty = false;
        LockReason = DocumentReadResult.Fail(DocumentReadStatus.Corrupted).UserMessage;
        Raise(nameof(Text));
        Raise(nameof(CanCopy));
        ClearSearch();
    }

    // ── 문서 안에서 찾기 (Ctrl+F) ────────────────────────────────────────────
    // 서비스가 필요 없다 — 이미 메모리에 Text 가 있다. 상태는 탭마다 따로 간다.

    private string _searchQuery = "";
    private List<int> _matches = [];
    private int _currentMatch = -1;

    /// View 가 그 자리를 보여달라는 신호. 값은 본문 안 문자 인덱스.
    /// Select() 만으로는 본문에 포커스가 없을 때 스크롤이 안 따라온다 [실측].
    public event EventHandler<int>? RevealRequested;

    /// 검색 띠가 양방향으로 묶는다. 타이핑할 때마다 다시 찾는다 —
    /// 문서 안 검색은 이미 메모리에 있는 문자열이라 비용이 없다 (20KB 1건에 0.003ms) [실측].
    public string SearchQuery
    {
        get => _searchQuery;
        set => SetSearch(value);
    }

    private bool _matchCase;

    /// 기본은 대소문자 무시. 켜면 정확히 같은 글자만 찾는다.
    public bool MatchCase
    {
        get => _matchCase;
        set
        {
            if (!Set(ref _matchCase, value)) return;

            RecomputeMatches(restart: true);
            if (_matches.Count > 0) RevealRequested?.Invoke(this, _matches[_currentMatch]);
        }
    }

    private StringComparison Rule
        => _matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// 강조 사각형을 그리려면 시작 위치와 길이가 둘 다 필요하다.
    public int MatchLength => _searchQuery.Length;

    public IReadOnlyList<int> Matches => _matches;

    /// 0-based. 일치가 없으면 -1.
    public int CurrentMatch
    {
        get => _currentMatch;
        private set { if (Set(ref _currentMatch, value)) Raise(nameof(MatchPositionText)); }
    }

    /// "3 / 12". 검색창에 포커스가 가면 본문 선택 강조가 화면에서 사라져서 [실측],
    /// 이 숫자가 없으면 사용자는 어디를 찾았는지 알 방법이 없다.
    public string MatchPositionText
        => _matches.Count == 0 ? "" : $"{_currentMatch + 1} / {_matches.Count}";

    public void SetSearch(string query)
    {
        var next = query ?? "";

        // 검색어가 바뀌면 첫 일치부터 시작한다. 직전 검색의 순번을 물려받으면
        // 새 검색어가 엉뚱한 자리에서 시작하고 "2 / 2" 같은 숫자를 보여준다.
        // 같은 검색어를 다시 치는 경우(타이핑 중 본문 변경 등)는 보던 자리를 지킨다.
        var restart = !string.Equals(next, _searchQuery, StringComparison.Ordinal);

        if (Set(ref _searchQuery, next)) Raise(nameof(MatchLength));
        RecomputeMatches(restart);

        if (_matches.Count > 0) RevealRequested?.Invoke(this, _matches[_currentMatch]);
    }

    public void ClearSearch()
    {
        // 강조가 남으면 화면에 어느 값이 민감한지 표시해 두는 셈이 된다
        if (Set(ref _searchQuery, "")) Raise(nameof(MatchLength));
        RecomputeMatches(restart: true);
    }

    /// <summary>
    /// 이 상자에서 선택한 글자만 클립보드로 보낸다. 검색창처럼 본문이 아닌 곳에서 쓴다 —
    /// <see cref="Copy"/> 는 선택이 없으면 문서 전체를 복사하므로 검색창에 쓰면 안 된다.
    /// 기록 제외·30초 자동 비움 경로는 그대로 탄다 (D-007).
    /// </summary>
    public void CopyPlain(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;

        _clipboard.Copy(text);
    }

    public bool MoveToNextMatch() => Step(+1);

    public bool MoveToPrevMatch() => Step(-1);

    private bool Step(int direction)
    {
        if (_matches.Count == 0) return false;

        // 끝에서 멈추면 "더 없다"와 "한 바퀴 돌았다"가 구분되지 않는다
        CurrentMatch = (_currentMatch + direction + _matches.Count) % _matches.Count;
        RevealRequested?.Invoke(this, _matches[_currentMatch]);
        return true;
    }

    private void RecomputeMatches(bool restart = false)
    {
        var previous = restart ? 0 : _currentMatch;
        _matches = FindAll(_text, _searchQuery);

        Raise(nameof(Matches));

        // 본문이 줄어들면 현재 자리가 범위를 벗어난다
        CurrentMatch = _matches.Count == 0 ? -1 : Math.Clamp(previous < 0 ? 0 : previous, 0, _matches.Count - 1);
        Raise(nameof(MatchPositionText));
    }

    /// <summary>
    /// 겹치지 않게 찾는다 — 'aaaa' 에서 'aa' 는 3이 아니라 2다.
    /// 비교는 OrdinalIgnoreCase 로 고정한다. 문화권 비교는 일치 길이가 검색어 길이와
    /// 달라질 수 있어(ß ↔ ss) 강조 사각형이 어긋난다 [실측].
    /// </summary>
    private List<int> FindAll(string text, string query)
    {
        var found = new List<int>();
        if (string.IsNullOrWhiteSpace(query) || text.Length < query.Length) return found;

        var from = 0;
        while (from <= text.Length - query.Length)
        {
            var at = text.IndexOf(query, from, Rule);
            if (at < 0) break;

            found.Add(at);
            from = at + query.Length;
        }
        return found;
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set { if (Set(ref _isDirty, value)) { Raise(nameof(StatusText)); Raise(nameof(SaveState)); } }
    }

    public bool IsReadOnly
    {
        get => _isReadOnly;
        private set { if (Set(ref _isReadOnly, value)) { Raise(nameof(StatusText)); Raise(nameof(SaveState)); Raise(nameof(ShowFormatBar)); } }
    }

    public string? LockReason
    {
        get => _lockReason;
        private set => Set(ref _lockReason, value);
    }

    /// <summary>
    /// 이 탭이 지금 보이는 탭인가. 본문은 탭마다 한 벌씩 만들어 두고 활성 탭만 보인다 —
    /// TabControl 의 ContentTemplate 은 TextBox 인스턴스를 하나만 만들어 돌려 써서,
    /// 본문이 같은 두 문서 사이에 실행취소 스택이 넘어간다 [실측] (F-1).
    /// 값은 ShellViewModel.ActiveTab 이 관리한다.
    /// </summary>
    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public async Task LoadAsync(string fullPath)
    {
        // 탭 키가 되므로 반드시 정규화한다. 안 하면 대소문자만 다른 경로가
        // 별개 키가 되어 같은 문서가 탭 두 개로 열린다.
        fullPath = PathRules.NormalizeFull(fullPath);

        var result = await _store.LoadAsync(fullPath);
        CurrentPath = fullPath;
        IsSnapshot = false;
        _loadStatus = result.Status;
        _binding = result.Binding;

        var isPlain = result.IsOk && result.Kind == DocumentKind.PlainText;
        var tooBig = isPlain && result.Text!.Length > MaxDisplayChars;

        if (result.IsOk && !tooBig)
        {
            // 평문은 되돌릴 사본을 남기지 않는다 (D-117) — 남기면 암호화되지 않은 사본이 숨김 폴더에 하나 더 생기고,
            // 그 사본은 이름이 opened.tbx 라 앱이 다시 읽지도 못한다.
            if (!isPlain) _store.CaptureOpenSnapshot(fullPath);

            _text = result.Text!;
            _rich = result.Rich ?? [];

            // 평문도 고칠 수 있다 (D-117). 읽은 형식 그대로 다시 쓴다 (D-118)
            IsReadOnly = false;
            LockReason = null;
            _loadFailed = false;
            _isPlainText = isPlain;
            _encodingLabel = result.EncodingLabel;
            _encodingIsGuess = result.EncodingIsGuess;
            _plainFormat = result.PlainFormat;
        }
        else
        {
            // 읽기에 성공하지 못한 모든 경우를 잠근다 (D-005).
            // 본문을 비워두고 저장을 막지 않으면 빈 편집기가 원본을 덮는다.
            _text = "";
            _rich = [];
            IsReadOnly = true;
            LockReason = tooBig ? TooBigNotice(result.Text!.Length) : result.UserMessage;
            _loadFailed = true;
            _isPlainText = false;
            _encodingLabel = null;
            _encodingIsGuess = false;
            _plainFormat = null;
        }

        Finish();
    }

    private static string TooBigNotice(int length)
        => $"내용이 너무 길어({length:N0}자) 화면에 띄우지 않습니다. 표시 한도는 {MaxDisplayChars:N0}자입니다. "
           + "검색에는 그대로 포함됩니다.";

    /// <summary>
    /// 이 문서를 열었을 때의 상태를 읽기 전용으로 연다 (D-016).
    /// 자동 복원은 하지 않는다 — 부분만 되살리고 싶은 경우가 더 흔하다. 복사해 붙여넣게 한다.
    /// </summary>
    public async Task LoadOpenSnapshotAsync(string snapshotPath, string documentName)
    {
        snapshotPath = PathRules.NormalizeFull(snapshotPath);
        var result = await _store.LoadAsync(snapshotPath);

        IsSnapshot = true;
        _loadStatus = result.Status;
        _binding = null;                   // 스냅샷에는 저장하지 않는다
        _snapshotOwnerName = documentName;
        _isPlainText = false;              // 스냅샷은 언제나 금고 문서다
        _encodingLabel = null;

        // CurrentPath 를 원본이 아니라 스냅샷으로 둔다. 원본으로 두면
        // 옛 내용이 원본에 저장되는 경로가 열린다.
        CurrentPath = snapshotPath;
        _text = result.IsOk ? result.Text! : "";
        _rich = result.IsOk ? result.Rich ?? [] : [];
        _loadFailed = !result.IsOk;
        IsReadOnly = true;                       // 스냅샷에는 어떤 경우에도 저장하지 않는다
        LockReason = result.IsOk
            ? $"'{documentName}' 을(를) 열었을 때의 상태입니다. 읽기 전용이니 필요한 부분을 복사해 문서에 붙여넣으세요."
            : result.UserMessage;

        Finish();
    }

    private void Finish()
    {
        _autoSave.Stop();          // 이전 문서에 대한 예약 저장이 새 문서에 적용되면 안 된다
        _lastSaveFailed = false;
        Raise(nameof(Rich));       // 화면이 이것을 보고 서식 문서를 새로 만든다
        Raise(nameof(Text));
        Raise(nameof(CanCopy));
        Raise(nameof(TabTitle));
        Raise(nameof(DocumentName));
        Raise(nameof(IsPlainText));
        Raise(nameof(EncodingLabel));
        IsDirty = false;
        Raise(nameof(StatusText));
        Raise(nameof(SaveState));

        // 검색 상태는 문서에 속한다. 안 지우면 새 문서에서 옛 검색어의 자리가 강조된다.
        ClearSearch();
    }

    /// 사용자가 명시적으로 저장을 요구한 경로(Ctrl+S). 실패하면 알린다.
    public Task<bool> TrySaveAsync() => SaveCoreAsync(notifyUser: true);

    private async Task<bool> SaveCoreAsync(bool notifyUser)
    {
        _autoSave.Stop();

        if (_disposed) return false;                  // 닫힌 탭이 디스크에 쓰면 사용자는 닫았다고 믿는다
        if (CurrentPath is null) return false;
        if (IsReadOnly) return false;                 // 잠긴 문서의 저장 경로를 완전히 차단
        if (!IsDirty) return true;

        // 같은 탭의 저장은 한 번에 하나다. 자동 저장이 도는 사이 닫기 저장이 겹치면, 먼저 시작해 늦게 끝난
        // 자동 저장이 옛 본문으로 새 본문을 덮는다 — 닫기 저장은 이미 성공해 탭이 닫힌 뒤다 [실측 — U-4 재현].
        await _saveGate.WaitAsync();
        try
        {
            // 기다리는 사이 탭이 닫혔거나, 앞 저장이 이 본문까지 이미 저장했다. 경로는 이름 변경·이동을 따라간 지금 것을 쓴다
            if (_disposed || CurrentPath is not { } path || IsReadOnly) return false;
            if (!IsDirty) return true;

            return await SaveOnceAsync(path, notifyUser);
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private readonly SemaphoreSlim _saveGate = new(1, 1);

    private async Task<bool> SaveOnceAsync(string path, bool notifyUser)
    {
        // 보낼 본문을 잡아 둔다. 저장을 기다리는 사이 사용자가 친 글자는 이번 저장에 없다 —
        // 끝난 뒤 "수정됨"을 무조건 끄면 그 글자는 다시 저장되지 않고, 탭을 닫으면 사라진다 [실측].
        // 서식만 바꾼 입력도 지켜야 해서 글자가 아니라 편집 버전으로 본다 (D-129).
        var version = _editVersion;
        try
        {
            // 평문은 키 · 암호화 경로를 타지 않는 따로 된 갈래로 저장한다 (D-120)
            if (_isPlainText)
            {
                await _store.SavePlainAsync(path, Text, _plainFormat!);
            }
            else
            {
                var body = CaptureBody?.Invoke() ?? new DocumentBody(Text, _rich);
                await _store.SaveAsync(path, body, _binding);
                _rich = body.Rich;
            }

            // 그 사이 입력이 있었으면 수정됨을 유지한다. 입력이 이미 자동 저장을 다시 예약했다.
            if (_editVersion == version) IsDirty = false;
            _lastSaveFailed = false;
            _saveFailReason = null;
            SaveBlockedByKey = false;
            Raise(nameof(StatusText));
            Raise(nameof(SaveState));
            return true;
        }
        catch (Exception ex)
        {
            // 경로와 예외 타입까지만. 내용은 절대 싣지 않는다 (PROHIBITED-CUSTOM-04)
            AppLog.Error("save", path, ex);
            _lastSaveFailed = true;
            _saveFailReason = ex is PlainTextEncodeException ? ex.Message : null;
            SaveBlockedByKey = ex is KeyUnavailableException;
            Raise(nameof(StatusText));
            Raise(nameof(SaveState));

            if (notifyUser)
            {
                var reason = ex is LinkedPathException or KeyUnavailableException or PlainTextEncodeException
                    ? $"\n\n{ex.Message}"
                    : $"\n({ex.GetType().Name})";
                _dialogs.Error("저장 실패", $"문서를 저장하지 못했습니다.\n\n{path}{reason}");
            }

            return false;                              // IsDirty는 그대로 유지된다
        }
    }

    /// <summary>
    /// 이름변경·이동으로 파일 경로만 바뀐 경우. 본문과 수정 상태는 건드리지 않는다.
    /// </summary>
    public void UpdatePath(string fullPath) => CurrentPath = PathRules.NormalizeFull(fullPath);

    /// <summary>
    /// 이동 직전에 부른다. 예약된 저장이 살아 있으면 옛 경로로 나가
    /// 옛 자리에 유령 문서를 만든다 — DocumentStore 의 방어는 "폴더가 사라진 경우"만 잡는다.
    /// </summary>
    public void CancelPendingSave() => _autoSave.Stop();

    /// 이 경로(또는 그 하위)가 지금 편집기에 열려 있는가.
    public bool IsAffectedBy(string fullPath)
        => CurrentPath is not null && PathRules.IsInsideRoot(fullPath, CurrentPath);

    /// <summary>
    /// 문서를 떠나기 전에 부른다. 자동 저장이므로 묻지 않고 바로 저장한다 (D-015).
    /// 저장에 실패하면 false — 호출자가 전환/종료를 취소해 편집 내용을 지킨다 (D-011).
    /// </summary>
    public Task<bool> ConfirmLeaveAsync() => SaveUntilCleanAsync(notifyUser: true);

    /// <summary>
    /// 탭을 닫거나 항목을 옮기기 전에 부른다. 대화상자를 띄우지 않는다 —
    /// 호출자가 "어느 탭 때문에 무엇을 못 했는지"까지 담아 한 번만 알린다.
    /// 편집기와 셸이 각자 띄우면 모달 두 개가 겹쳐 뜨고 사용자는 뒤엣것만 읽는다.
    /// </summary>
    public Task<bool> TrySaveForLeaveAsync() => SaveUntilCleanAsync(notifyUser: false);

    /// <summary>
    /// 떠나기 전 저장. 저장을 기다리는 사이 친 글자가 있으면 SaveCoreAsync 는 수정됨을 유지한 채 true 를 돌려준다 —
    /// 그것을 "다 저장됨"으로 믿고 탭을 닫거나 종료하면 그 글자가 사라진다(단일 탭 닫기 · 종료, 적대적 검토).
    /// 깨끗해질 때까지 몇 번 저장한다. 계속 바뀌면 실패로 돌려 호출자가 닫기를 멈추게 한다.
    /// </summary>
    private async Task<bool> SaveUntilCleanAsync(bool notifyUser)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!IsDirty || IsReadOnly) return true;      // 읽기 전용은 잃을 편집 내용이 없다
            if (!await SaveCoreAsync(notifyUser)) return false;
        }

        return !IsDirty;
    }

    public void Copy(string? selectedText)
    {
        // 서식 문서의 「전체 복사」는 서식째 보낸다 (D-126). 빈지 아닌지는 뽑은 본문이 말한다(CopyRich)
        if (string.IsNullOrEmpty(selectedText) && !_isPlainText && CaptureAllForCopy is { } all)
        {
            if (HasDocument && !_loadFailed) CopyRich(all());
            return;
        }

        if (!CanCopy) return;

        var payload = string.IsNullOrEmpty(selectedText) ? Text : selectedText;
        if (payload.Length == 0) return;

        _clipboard.Copy(payload);
    }

    /// 서식 본문에서 고른 것(또는 전체)을 서식째 보낸다. 기록 제외 · 30초 자동 비움 경로는 같다 (D-007 · D-126).
    public void CopyRich(ClipboardPayload payload)
    {
        // CanCopy 는 보지 않는다 — 검색용 글자는 입력 뒤 조금 늦게 맞춰져, 빈 문서에 치자마자 복사하면 "빈 문서"로 보인다 [실측 — 시험]
        if (!HasDocument || _loadFailed || payload.Text.Length == 0) return;

        _clipboard.Copy(payload);
    }

    /// <summary>
    /// 탭을 닫을 때 부른다. 빠뜨리면 닫힌 탭이 1.5초 뒤 디스크에 쓰고,
    /// 복호화된 평문을 든 편집기가 프로세스 종료까지 남는다 [실측].
    /// AutoSaveTimer.Dispose 는 Stop 한 줄이라 되살아날 수 있어, 편집기 자신이 해제 상태를 든다.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoSave.Elapsed -= OnAutoSaveElapsed;
        _autoSave.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// 탭이 닫혔다. 화면이 이 문서에 쓰려고 메모리에 올린 그림 원본을 내린다 (D-146).
    public event EventHandler? Closed;

    // ── 그림 넣기 (D-140) ────────────────────────────────────────────────────
    // 파일 고르기 창과 오류 알림만 맡는다. 파일을 읽는 것은 화면 쪽 ImageFileImport 한 곳이다 (CUSTOM-03 · 05 예외).

    public string? PickImageFile() => _dialogs.PickImageFile();

    /// 문구에 경로 · 파일 이름을 넣지 않는다 — 금고 밖 이름이다 (PROHIBITED-CUSTOM-04).
    public void ImageInsertFailed(string reason, Exception? cause)
    {
        AppLog.Warn("image-import", null, cause);
        _dialogs.Error("그림 넣기", reason);
    }
}
