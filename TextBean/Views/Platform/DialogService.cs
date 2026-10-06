using System.Windows;
using Microsoft.Win32;
using TextBean.Models;
using TextBean.Services;
using TextBean.Services.Interfaces;
using TextBean.Views.Dialogs;

namespace TextBean.Views.Platform;

public sealed class DialogService : IDialogService
{
    private static Window? Owner => Application.Current?.MainWindow;

    // UI 스레드에서만 오간다 — 대화상자는 UI 스레드에서만 뜬다
    private int _showing;
    private int _suppressed;

    public bool IsShowing => _showing > 0;

    /// <summary>
    /// MessageBox 를 띄우는 한 지점(주인 창 찾기 포함). 시험은 바꿔 끼운다 — MessageBox 는 시험 호스트의 어떤 걸쇠에도
    /// 걸리지 않아 억제가 깨지면 사용자 화면에 진짜 창이 뜬다 [실측 — 계획 검토] (D-104).
    /// </summary>
    public Func<string, string, MessageBoxButton, MessageBoxImage, MessageBoxResult> ShowMessage { get; set; } = ShowMessageBox;

    // owner가 null일 때 MessageBox.Show(Window, ...) 오버로드는 내부에서 WindowInteropHelper를
    // 만들다 ArgumentNullException을 던진다. `!`는 컴파일 경고만 지울 뿐 런타임을 지켜주지 않는다.
    // 창이 만들어지기 전(App.ResolveRoot)에 오류를 띄우는 경로가 실제로 있으므로 갈라 쓴다.
    private static MessageBoxResult ShowMessageBox(string message, string title, MessageBoxButton button, MessageBoxImage image)
        => Owner is { } owner
            ? MessageBox.Show(owner, message, title, button, image)
            : MessageBox.Show(message, title, button, image);

    public IDisposable Suppress()
    {
        _suppressed++;
        return new SuppressScope(this);
    }

    private sealed class SuppressScope(DialogService owner) : IDisposable
    {
        private bool _ended;

        public void Dispose()
        {
            if (_ended) return;
            _ended = true;
            owner._suppressed--;
        }
    }

    /// 억제 중이면 띄우지 않는다. 제목 · 본문은 적지 않는다 — 문서 이름이 들어 있다 (PROHIBITED-CUSTOM-04).
    private bool Suppressed(string kind)
    {
        if (_suppressed == 0) return false;

        AppLog.Warn($"dialog-suppressed-{kind}", null, null);
        return true;
    }

    /// 떠 있는 동안 IsShowing 이 참이다. MessageBox 는 스레드 모달에 걸리지 않아 따로 센다 (D-104).
    private T Showing<T>(Func<T> show)
    {
        _showing++;
        try { return show(); }
        finally { _showing--; }
    }

    public string? PickFolder(string suggestedPath, string title)
    {
        if (Suppressed("pick-folder")) return null;

        var dialog = new OpenFolderDialog
        {
            Title = title,
            InitialDirectory = suggestedPath,
            Multiselect = false
        };

        return Showing(() => dialog.ShowDialog()) == true ? dialog.FolderName : null;
    }


    public string? PromptText(string title, string initial)
    {
        if (Suppressed("prompt-text")) return null;

        var dialog = new TextPromptDialog(title, initial);
        var owner = Owner;
        if (owner is not null) dialog.Owner = owner;

        return Showing(() => dialog.ShowDialog()) == true ? dialog.Value : null;
    }

    public KeyEntry? PromptKey(string title, string message, bool confirm)
    {
        if (Suppressed("prompt-key")) return null;

        var dialog = new KeyPromptDialog(title, message, confirm);
        var owner = Owner;
        if (owner is not null) dialog.Owner = owner;

        try
        {
            return Showing(() => dialog.ShowDialog()) == true ? dialog.Entry : null;
        }
        finally
        {
            dialog.ClearSecrets();
        }
    }

    public string? PickDocument(string suggestedFolder, string title)
    {
        if (Suppressed("pick-document")) return null;

        var dialog = new OpenFileDialog
        {
            Title = title,
            InitialDirectory = suggestedFolder,
            // 트리에 뜨는 것과 같은 허용 목록을 쓴다. .tbx 만 두면 트리에서는 보이는 평문 파일을
            // "이 문서" 범위로 고를 수 없어, 사용자가 찾을 수 있는 것과 좁힐 수 있는 것이 어긋난다.
            Filter = "TextBean 문서와 텍스트 파일 (*.tbx;*.txt)|*.tbx;*.txt"
                     + "|TextBean 문서 (*.tbx)|*.tbx"
                     + "|텍스트 파일 (*.txt)|*.txt",
            CheckFileExists = true,
            Multiselect = false
        };

        return Showing(() => dialog.ShowDialog()) == true ? dialog.FileName : null;
    }

    public string? PickImageFile()
    {
        if (Suppressed("pick-image")) return null;

        var dialog = new OpenFileDialog
        {
            Title = "그림 넣기",
            Filter = "그림 파일 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif",
            CheckFileExists = true,
            Multiselect = false
        };

        return Showing(() => dialog.ShowDialog()) == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message)
    {
        if (Suppressed("confirm")) return false;

        var result = Showing(() => ShowMessage(message, title, MessageBoxButton.OKCancel, MessageBoxImage.Question));

        return result == MessageBoxResult.OK;
    }

    public bool ConfirmHideToTray()
    {
        if (Suppressed("tray-notice")) return false;

        var dialog = new TrayNoticeDialog();
        var owner = Owner;
        if (owner is not null) dialog.Owner = owner;

        return Showing(() => dialog.ShowDialog()) == true;
    }

    public void Error(string title, string message)
    {
        if (Suppressed("error")) return;

        Showing(() => ShowMessage(message, title, MessageBoxButton.OK, MessageBoxImage.Warning));
    }
}
