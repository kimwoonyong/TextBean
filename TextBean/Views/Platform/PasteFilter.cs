using System.Windows;

namespace TextBean.Views.Platform;

public enum PasteChoice { Rich, PlainText, Image, Reject }

/// <summary>
/// 서식 본문에 무엇을 붙여넣을지 (D-126). 앱 안에서 복사한 서식(표지 + XamlPackage)만 서식째 받고,
/// 밖에서 온 것은 글자만 받는다 — 웹 · 워드의 글꼴 · 크기 · 배경이 문서에 섞이지 않게.
/// 글자가 없으면 그림(캡처 등)만 받는다. 파일 · 그 밖은 받지 않는다.
/// </summary>
public static class PasteFilter
{
    public static PasteChoice Choose(IDataObject data)
    {
        if (data.GetDataPresent(ClipboardService.RichMarkerFormat, autoConvert: false)
            && data.GetDataPresent(DataFormats.XamlPackage, autoConvert: false))
            return PasteChoice.Rich;

        // 글자가 먼저다 — 엑셀 · 워드는 글자와 함께 그림(Bitmap)도 준다. 글자 없이 그림만이면 그림 (D-144)
        if (data.GetDataPresent(DataFormats.UnicodeText, autoConvert: true)) return PasteChoice.PlainText;
        return data.GetDataPresent(DataFormats.Bitmap, autoConvert: true) ? PasteChoice.Image : PasteChoice.Reject;
    }
}
