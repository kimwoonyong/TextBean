namespace TextBean.ViewModels;

/// 바꿀 수 있는 앱 동작 하나. 기본 키가 null 이면 처음에는 단축키가 없다.
public sealed record ShortcutAction(string Id, string Name, string? DefaultKey);

/// 바꿀 수 없는 키 한 줄 (목록 창 표시용).
public sealed record FixedShortcut(string Name, string Keys);

/// <summary>
/// 단축키 동작 목록과 검사 규칙 (D-110~D-114). 키는 글자로 다룬다 — "Ctrl+Alt+Shift+키" 순서, 키 이름은 화면 쪽
/// (ShortcutsDialog.ToText)이 같은 규칙으로 만든다. ViewModel 은 WPF 형식을 모른다.
/// </summary>
public static class ShortcutCatalog
{
    public static IReadOnlyList<ShortcutAction> Actions { get; } =
    [
        new("save", "지금 탭 저장", "Ctrl+S"),
        new("closeTab", "탭 닫기", "Ctrl+W"),
        new("findBar", "검색", "Ctrl+F"),
        new("searchVault", "상세 검색", "Ctrl+Shift+F"),
        new("findNext", "다음 찾기", "F3"),
        new("findPrev", "이전 찾기", "Shift+F3"),
        new("closeFindBar", "찾기 띠 닫기", "Esc"),
        new("refresh", "새로고침", "F5"),
        new("shortcuts", "단축키 보기", "F1"),
        new("exit", "종료", "Ctrl+Q"),
        new("newDocument", "새 문서", null),
        new("newFolder", "새 폴더", null),
        new("rename", "이름 변경", null),
        new("delete", "삭제", null),
        new("copyAll", "전체 복사", null),
        new("openBackup", "열었을 때 상태 보기", null),
        new("emptyTrash", "휴지통 비우기", null),
        new("changeRoot", "금고 폴더 변경", null),
        new("changeKey", "키 입력 · 변경", null),
        new("lock", "잠그기", null),
        new("closeAllTabs", "모든 탭 닫기", null),
        new("openInExplorer", "탐색기에서 열기", null),
        new("voiceInput", "음성 입력", null),
    ];

    /// 바꿀 수 없는 키 — 목록 창 아래에 회색으로 보인다 (D-111).
    public static IReadOnlyList<FixedShortcut> FixedKeys { get; } =
    [
        new("복사 · 잘라내기 · 붙여넣기", "Ctrl+C · X · V"),
        new("실행취소 · 다시 실행", "Ctrl+Z · Y"),
        new("모두 선택", "Ctrl+A"),
        new("찾기 칸 다음 / 이전", "Enter / Shift+Enter"),
        new("굵게 · 기울임 · 밑줄", "Ctrl+B · I · U"),
        new("취소선", "Ctrl+Shift+X"),
        new("글자 크기 크게 · 작게", "Ctrl+] · Ctrl+["),
        new("표 칸 다음 · 이전", "Tab · Shift+Tab"),
    ];

    /// <summary>
    /// 고를 수 없는 키 (D-112 · D-127). 본문(TextBox · RichTextBox)이 먼저 쓰는 키는 창의 단축키까지 오지 않는다
    /// [실측 — add-shortcut-settings/textbox-keys-probe.txt]. 탭 전환 키는 탭 줄(TabControl)이, Alt+F4 · Alt+Space 는 Windows 가 쓴다.
    /// </summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "Ctrl+A", "Ctrl+C", "Ctrl+V", "Ctrl+X", "Ctrl+Y", "Ctrl+Z", "Ctrl+Insert", "Shift+Insert", "Shift+Delete",
        "Backspace", "Ctrl+Backspace", "Shift+Backspace", "Alt+Backspace", "Delete", "Ctrl+Delete", "Insert",
        "Enter", "Shift+Enter", "Tab", "Shift+Tab", "Space", "Shift+Space",
        "Left", "Right", "Up", "Down", "Home", "End", "PageUp", "PageDown",
        "Ctrl+Left", "Ctrl+Right", "Ctrl+Up", "Ctrl+Down", "Ctrl+Home", "Ctrl+End",
        "Shift+Left", "Shift+Right", "Shift+Up", "Shift+Down", "Shift+Home", "Shift+End", "Shift+PageUp", "Shift+PageDown",
        "Ctrl+Shift+Left", "Ctrl+Shift+Right", "Ctrl+Shift+Up", "Ctrl+Shift+Down", "Ctrl+Shift+Home", "Ctrl+Shift+End",
        "Alt+PageUp", "Alt+PageDown", "Alt+Shift+PageUp", "Alt+Shift+PageDown",
        "Ctrl+Alt+PageUp", "Ctrl+Alt+PageDown", "Ctrl+Alt+Shift+PageUp", "Ctrl+Alt+Shift+PageDown",
        "Ctrl+Tab", "Ctrl+Shift+Tab", "Ctrl+PageUp", "Ctrl+PageDown",
        "Alt+F4", "Alt+Space",
        // 서식 본문 (D-127): 서식 키, 그리고 막아 둔 RichTextBox 기본 키 — 막은 키도 본문이 먼저 받아 창까지 오지 않는다
        "Ctrl+B", "Ctrl+I", "Ctrl+U", "Ctrl+Shift+X",
        "Ctrl+E", "Ctrl+L", "Ctrl+R", "Ctrl+J", "Ctrl+1", "Ctrl+2", "Ctrl+5", "Ctrl+T", "Ctrl+Shift+T",
        "Ctrl+Shift+L", "Ctrl+Shift+N", "Ctrl+Shift+R", "Ctrl+Space", "Ctrl+Shift+C",
        "Ctrl+OemOpenBrackets", "Ctrl+Oem4", "Ctrl+OemCloseBrackets", "Ctrl+Oem6", "Ctrl+OemPlus", "Ctrl+Shift+OemPlus",
    };

    public static ShortcutAction? Find(string id) => Actions.FirstOrDefault(a => a.Id == id);

    /// 고를 수 있는 키면 null, 아니면 이유.
    public static string? Reject(string key)
    {
        if (Reserved.Contains(key)) return $"{key} 는 본문 편집이나 Windows 가 쓰는 키라 고를 수 없습니다.";

        // 글자 · 숫자 하나는 본문에 글자가 들어간다 — Ctrl 이나 Alt 가 있어야 한다 (WPF KeyGesture 규칙과 같다)
        var parts = key.Split('+');
        var name = parts[^1];
        var bare = parts.Length == 1 || parts[..^1].All(m => m == "Shift");
        if (bare && name.Length == 1 && char.IsLetterOrDigit(name[0]))
            return "글자 · 숫자 키는 Ctrl 이나 Alt 와 함께 눌러야 합니다.";

        return null;
    }

    /// <summary>
    /// 저장된 값으로 지금 키 표를 만든다. 없는 id 는 기본 키, 빈 글자는 「없음」.
    /// 막힌 키 · 다른 동작과 겹치는 키는 그 줄만 기본 키로(그것도 겹치면 없음) 되돌리고 <paramref name="invalid"/> 로 알린다.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Resolve(IReadOnlyDictionary<string, string>? saved, Action<string>? invalid = null)
    {
        var map = new Dictionary<string, string?>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var action in Actions)
        {
            string? key = action.DefaultKey;
            if (saved is not null && saved.TryGetValue(action.Id, out var stored))
            {
                if (stored.Length == 0) key = null;
                else if (Reject(stored) is null && !used.Contains(stored)) key = stored;
                else invalid?.Invoke(action.Id);
            }

            if (key is not null && used.Contains(key)) key = null;
            if (key is not null) used.Add(key);
            map[action.Id] = key;
        }

        return map;
    }

    public static IReadOnlyDictionary<string, string?> Defaults => Resolve(null);
}
