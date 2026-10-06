using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TextBean.Models;
using TextBean.Services.Interfaces;

namespace TextBean.Views.Platform;

/// <summary>
/// WPF에 의존하므로 Services/ 가 아니라 Views/ 아래 둔다 (D-013, PROHIBITED-ARCH-02).
/// </summary>
public sealed class ClipboardService : IClipboardService
{
    // J-04 확정: 30초 상수. 설정으로 노출하지 않는다.
    private static readonly TimeSpan ClearAfter = TimeSpan.FromSeconds(30);

    // 클립보드 API는 STA 전용. 풀 스레드 타이머로 깨우면 ThreadStateException이
    // 조용히 나고 자동 비움이 영영 동작하지 않는다.
    private readonly DispatcherTimer _timer;
    private string? _lastCopyId;

    public ClipboardService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = ClearAfter };
        _timer.Tick += (_, _) => { _timer.Stop(); ClearIfOurs(); };
    }

    /// 앱 안에서 복사한 서식이라는 표지. 붙여넣기는 이것이 있을 때만 서식을 받는다 (D-126).
    public const string RichMarkerFormat = "TextBean.Rich";

    /// 복사마다 새로 싣는 표지 (D-143). 30초 비움은 클립보드에 이 값이 그대로일 때만 지운다.
    public const string CopyIdFormat = "TextBean.CopyId";

    public void Copy(string text) => Copy(new ClipboardPayload(text));

    public void Copy(ClipboardPayload payload)
    {
        var copyId = Guid.NewGuid().ToString("N");
        var data = BuildDataObject(payload, copyId);

        if (!TryClipboard(() => Clipboard.SetDataObject(data, copy: true))) return;

        _lastCopyId = copyId;
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>
    /// 클립보드에 넣을 DataObject. 서식 · 그림 형식도 같은 DataObject 에 담는다 — 플래그는 DataObject 단위라
    /// 형식마다 따로 넣으면 플래그 없는 판이 기록으로 나간다. 시험은 클립보드를 쓰지 않고 이것만 본다.
    /// </summary>
    public static DataObject BuildDataObject(ClipboardPayload payload, string? copyId = null)
    {
        var data = new DataObject();
        data.SetText(payload.Text);
        data.SetData(CopyIdFormat, copyId ?? Guid.NewGuid().ToString("N"));

        if (payload.Rtf is not null) data.SetData(DataFormats.Rtf, payload.Rtf);
        if (payload.XamlPackage is not null)
        {
            data.SetData(DataFormats.XamlPackage, new MemoryStream(payload.XamlPackage, writable: false));
            data.SetData(RichMarkerFormat, "1");
        }

        // 그림 하나만 골랐을 때 — 그림판은 Bitmap, 브라우저 · 메신저는 PNG 를 읽는다
        if (payload.Png is not null)
        {
            var frame = BitmapFrame.Create(new MemoryStream(payload.Png, writable: false), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            data.SetData(DataFormats.Bitmap, frame);
            data.SetData("PNG", new MemoryStream(payload.Png, writable: false));
        }

        // 이 플래그가 없으면 복호화한 평문이 Win+V 기록과 클라우드 클립보드에 남아
        // 암호화가 통째로 무의미해진다 (D-007). 값 타입은 Task 9에서 실측 확정한다.
        data.SetData("CanIncludeInClipboardHistory", false);
        data.SetData("CanUploadToCloudClipboard", false);
        data.SetData("ExcludeClipboardContentFromMonitorProcessing", false);
        return data;
    }

    public void ClearIfOurs()
    {
        if (_lastCopyId is null) return;

        var lastCopyId = _lastCopyId;
        TryClipboard(() =>
        {
            // 값을 확인하지 않고 지우면 그 사이 사용자가 복사한 남의 내용을 날린다
            if (IsOurs(Clipboard.GetDataObject(), lastCopyId)) Clipboard.Clear();
        });

        _lastCopyId = null;
    }

    /// <summary>
    /// 지금 클립보드가 마지막으로 우리가 넣은 것인가 — 글자가 아니라 복사 표지로 본다 (D-143).
    /// 그림만 복사하면 글자가 공백 한 칸이라, 글자로는 남이 복사한 공백과 가를 수 없다 [실측].
    /// </summary>
    public static bool IsOurs(IDataObject? current, string copyId)
        => current?.GetDataPresent(CopyIdFormat) == true && current.GetData(CopyIdFormat) as string == copyId;

    private static bool TryClipboard(Action action)
    {
        // 다른 앱이 클립보드를 점유하면 예외가 난다. 짧게 재시도하되 UI를 동기 대기로 막지 않는다.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception) when (attempt < 2)
            {
                Thread.Yield();
            }
            catch (Exception)
            {
                // 조용히 삼키지 않는다 — 사용자는 복사됐다고 믿고 붙여넣는다
                MessageBox.Show("클립보드를 다른 프로그램이 사용 중입니다. 잠시 후 다시 시도해주세요.",
                    "복사 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
        }
        return false;
    }

    public void Dispose() => _timer.Stop();
}
