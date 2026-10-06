using System.IO;
using System.Windows.Media.Imaging;

namespace TextBean.Views.Platform;

/// <summary>
/// 「그림 넣기」로 고른 파일을 읽는다 — **금고 밖 파일을 읽는 유일한 곳** (D-140, PROHIBITED-CUSTOM-03 · 05 예외).
/// <list type="bullet">
/// <item>사용자가 그림 넣기 창에서 고른 파일 하나만 · 읽기만(쓰기 · 지우기 · 금고로 복사 없음)</item>
/// <item>그림 확장자만 · 20MB 이하만</item>
/// <item>오류 문구에 경로 · 파일 이름을 넣지 않는다 — 금고 밖 이름이다 (CUSTOM-04)</item>
/// </list>
/// 그림 내용은 문서에 들어가 문서와 함께 암호화된다. 원본 파일은 건드리지 않는다.
/// </summary>
public static class ImageFileImport
{
    public const long MaxBytes = 20L * 1024 * 1024;

    private static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif"];

    public static BitmapSource Load(string path)
    {
        if (!Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new ImageImportException("그림 파일(png · jpg · bmp · gif)만 넣을 수 있습니다.");

        byte[] bytes;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxBytes) throw new ImageImportException("20MB 보다 큰 그림은 넣을 수 없습니다.");

            bytes = new byte[stream.Length];
            stream.ReadExactly(bytes);
        }
        catch (ImageImportException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ImageImportException("그림 파일을 읽지 못했습니다.", ex);
        }

        try
        {
            var frame = BitmapFrame.Create(new MemoryStream(bytes, writable: false), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return frame;
        }
        catch (Exception ex)
        {
            throw new ImageImportException("그림으로 열 수 없는 파일입니다.", ex);
        }
    }
}

/// 그림 넣기 실패. Message 는 사용자에게 그대로 보인다 — 경로 · 파일 이름을 넣지 않는다.
public sealed class ImageImportException(string message, Exception? inner = null) : Exception(message, inner);
