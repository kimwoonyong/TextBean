using System.Windows;
using System.Windows.Input;
using TextBean.ViewModels;

namespace TextBean.Views.Dialogs;

/// <summary>
/// 단축키 목록 · 바꾸기 창 (D-110). 키를 받는 중에는 누른 키를 글자로 바꿔 ViewModel 에 넘긴다 —
/// 판정(막힌 키 · 겹침)은 ViewModel 이 한다 (PROHIBITED-UI-03). 키 글자 ↔ WPF 키 변환은 여기 한 곳이다.
/// </summary>
public partial class ShortcutsDialog : Window
{
    public ShortcutsDialog()
    {
        Platform.AppTheme.Use(this);         // InitializeComponent 앞에 (D-163)
        InitializeComponent();
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private ShortcutsViewModel Vm => (ShortcutsViewModel)DataContext;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Vm.IsCapturing) return;

        // Alt 조합은 SystemKey 로 온다
        var key = e.Key == Key.System ? e.SystemKey : e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (IsModifier(key)) return;              // 수식키만 눌린 동안은 기다린다

        e.Handled = true;                         // 목록 이동 · 창 닫기(Esc)로 새지 않게
        _ = Vm.CaptureAsync(ToText(key, Keyboard.Modifiers));
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ShortcutRow row }) return;

        Vm.Selected = row;
        if (Vm.ChangeCommand.CanExecute(null)) Vm.ChangeCommand.Execute(null);
        e.Handled = true;
    }

    private static bool IsModifier(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.None;

    // 같은 값의 이름이 여럿인 키(Next/PageDown 등)는 한 이름으로 맞춘다 — 막는 키 목록과 같은 이름
    private static readonly Dictionary<Key, string> Names = new()
    {
        [Key.PageDown] = "PageDown", [Key.PageUp] = "PageUp", [Key.Enter] = "Enter", [Key.Back] = "Backspace",
        [Key.Escape] = "Esc", [Key.CapsLock] = "CapsLock",
        [Key.D0] = "0", [Key.D1] = "1", [Key.D2] = "2", [Key.D3] = "3", [Key.D4] = "4",
        [Key.D5] = "5", [Key.D6] = "6", [Key.D7] = "7", [Key.D8] = "8", [Key.D9] = "9",
    };

    /// 키 + 수식키 → "Ctrl+Alt+Shift+키" 글자. ShortcutCatalog 의 기본 키 · 막는 키와 같은 규칙이다.
    public static string ToText(Key key, ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Names.TryGetValue(key, out var name) ? name : key.ToString());
        return string.Join("+", parts);
    }

    /// 키 글자 → WPF 단축키. 읽을 수 없거나 WPF 가 받지 않는 조합이면 null.
    public static KeyGesture? ToGesture(string text)
    {
        var parts = text.Split('+');
        var modifiers = ModifierKeys.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part switch
            {
                "Ctrl" => ModifierKeys.Control,
                "Alt" => ModifierKeys.Alt,
                "Shift" => ModifierKeys.Shift,
                "Win" => ModifierKeys.Windows,
                _ => (ModifierKeys)(-1),
            };
        }
        if ((int)modifiers < 0) return null;

        var keyName = parts[^1];
        var key = Names.FirstOrDefault(p => p.Value == keyName).Key;
        if (key == Key.None && !Enum.TryParse(keyName, out key)) return null;

        try { return new KeyGesture(key, modifiers); }
        catch (NotSupportedException) { return null; }
    }
}
