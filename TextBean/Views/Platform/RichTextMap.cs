using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;

namespace TextBean.Views.Platform;

/// <summary>
/// 서식 문서의 검색용 글자와 [글자 위치 → TextPointer] 대응을 <b>한 걸음에서</b> 만든다 (D-128).
/// TextRange.Text 로 글자를 뽑고 위치를 따로 세면 둘이 어긋난다 — 그림 · 표 · 문서 끝을 제각각 적는다 [실측].
/// 문단 사이와 LineBreak 는 "\r\n", 표의 칸 사이는 "\t" 이다. 100만 자 문서에서 한 번 걷는 데 약 45ms [실측].
/// </summary>
public sealed class RichTextMap
{
    private const string NewLine = "\r\n";
    private const string CellSeparator = "\t";

    private readonly List<(int Index, TextPointer Pointer, int Length)> _segments;

    private RichTextMap(string text, List<(int, TextPointer, int)> segments)
    {
        Text = text;
        _segments = segments;
    }

    public string Text { get; }

    public static RichTextMap Build(FlowDocument document)
    {
        var text = new StringBuilder();
        var segments = new List<(int, TextPointer, int)>();
        var firstParagraph = true;

        for (var at = document.ContentStart; at is not null; at = at.GetNextContextPosition(LogicalDirection.Forward))
        {
            switch (at.GetPointerContext(LogicalDirection.Forward))
            {
                case TextPointerContext.None:
                    return new RichTextMap(text.ToString(), segments);

                case TextPointerContext.Text:
                    var run = at.GetTextInRun(LogicalDirection.Forward);
                    segments.Add((text.Length, at, run.Length));
                    text.Append(run);
                    break;

                case TextPointerContext.ElementStart:
                    switch (at.GetAdjacentElement(LogicalDirection.Forward))
                    {
                        // 표는 TextRange.Text 와 같은 모양이다 — 칸 사이 Tab, 행 사이 · 표 앞뒤 줄바꿈 (D-135).
                        // 칸의 첫 문단은 행 · 칸 구분이 이미 섰으므로 아무것도 넣지 않는다.
                        case Paragraph { Parent: TableCell cell } paragraph when ReferenceEquals(cell.Blocks.FirstBlock, paragraph):
                            break;
                        case Paragraph:
                        case TableRow:
                            if (!firstParagraph) AddSeparator(text, segments, at, NewLine);
                            firstParagraph = false;
                            break;
                        case TableCell { Parent: TableRow row } cell when !ReferenceEquals(row.Cells[0], cell):
                            AddSeparator(text, segments, at, CellSeparator);
                            break;
                        case LineBreak:
                            AddSeparator(text, segments, at, NewLine);
                            break;
                    }
                    break;
            }
        }

        return new RichTextMap(text.ToString(), segments);
    }

    private static void AddSeparator(StringBuilder text, List<(int, TextPointer, int)> segments, TextPointer at, string separator)
    {
        segments.Add((text.Length, at, 0));
        text.Append(separator);
    }

    /// 글자 위치의 TextPointer. 줄바꿈 자리는 그 줄바꿈 앞을 가리킨다. 범위 밖이면 null.
    public TextPointer? PointerAt(int index)
    {
        if (index < 0 || index > Text.Length) return null;

        int low = 0, high = _segments.Count - 1, found = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (_segments[mid].Index <= index) { found = mid; low = mid + 1; }
            else high = mid - 1;
        }
        if (found < 0) return null;

        var (start, pointer, length) = _segments[found];
        if (length == 0) return index == start ? pointer : pointer.GetNextInsertionPosition(LogicalDirection.Forward) ?? pointer;
        return pointer.GetPositionAtOffset(Math.Min(index - start, length));
    }

    /// <summary>
    /// 서식 없는 본문(검색용 글자만 있는 문서 — 새 빈 문서 · 시험)을 문단으로 나눠 문서로 만든다.
    /// 줄바꿈은 \r\n · \n · \r 모두 문단 경계로 본다.
    /// </summary>
    public static FlowDocument FromPlain(string text)
    {
        var document = new FlowDocument();
        foreach (var line in text.ReplaceLineEndings("\n").Split('\n'))
            document.Blocks.Add(new Paragraph(new Run(line)));
        return document;
    }

    public static FlowDocument Load(byte[] package)
    {
        var document = new FlowDocument();
        using var stream = new MemoryStream(package, writable: false);
        new TextRange(document.ContentStart, document.ContentEnd).Load(stream, DataFormats.XamlPackage);
        return document;
    }

    public static byte[] Save(TextRange range)
    {
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.XamlPackage);
        return stream.ToArray();
    }

    public static string SaveRtf(TextRange range)
    {
        using var stream = new MemoryStream();
        range.Save(stream, DataFormats.Rtf);
        return Encoding.ASCII.GetString(stream.ToArray());   // RTF 는 7비트 — 한글은 \uN 으로 이스케이프된다
    }
}
