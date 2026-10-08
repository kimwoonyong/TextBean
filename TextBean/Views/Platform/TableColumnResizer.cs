using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using TextBean.Services;

namespace TextBean.Views.Platform;

/// <summary>
/// 표 열 너비 끌기 (D-170) · 표 전체 줄이기 (D-171). 열 경계에 마우스를 올리면 ↔, 누르고 끌면 폭이 바뀐다(손잡이는 그리지 않는다 — 사용자 판정).
/// <list type="bullet">
/// <item>열 경계: 그 왼쪽 열만 바뀐다 — 표 전체 폭이 따라 바뀐다. 맨 오른쪽 바깥선: 모든 열이 같은 비율로.</item>
/// <item>폭은 [24px, 끄는 순간 그려진 본문 폭] 안 — 넘으면 오른쪽이 잘리고 가로 스크롤도 없다 [실측].
///   그림용 <c>RichImage.AvailableWidth</c> 는 스크롤바까지 빼 지금 표 폭보다 작아 쓰지 않는다(계획 검토 R-2).</item>
/// <item>열 폭만 바꾸면 실행취소에 안 잡힌다 [실측]. 끄는 동안은 폭을 바로 바꾸고, 놓을 때 원래 폭으로 되돌린 뒤
///   새 폭을 단 복제 표로 한 번에 갈아 끼운다 — 실행취소 한 번 · 칸 안 그림도 산다 [실측 — 계획 검토 R-1].</item>
/// </list>
/// 이벤트 처리는 얇게 두고 계산은 좌표를 받는 메서드로 — 시험은 실제 마우스를 쓰지 않는다 (D-082).
/// </summary>
public static class TableColumnResizer
{
    /// 경계 양쪽으로 이만큼 안이면 경계로 본다.
    public const double Grip = 3;

    public const double MinColumnWidth = 24;

    /// 표의 세로 경계 하나. Index = 그 경계 왼쪽 열 번호 — 마지막 열이면 표 바깥선(② 표 전체).
    public sealed record Edge(Table Table, int Index, double X, double Top, double Bottom)
    {
        public bool IsOuter => Index == Table.Columns.Count - 1;
    }

    private sealed class Drag(Table table, int index, double startX, double[] startWidths, GridLength[] original, double max)
    {
        public Table Table { get; } = table;
        public int Index { get; } = index;
        public double StartX { get; } = startX;
        public double[] StartWidths { get; } = startWidths;
        public GridLength[] Original { get; } = original;
        public double Max { get; } = max;
        public double[] Current { get; set; } = startWidths;
    }

    private static readonly DependencyProperty DragProperty =
        DependencyProperty.RegisterAttached("Drag", typeof(Drag), typeof(TableColumnResizer));

    public static void Attach(RichTextBox box)
    {
        box.AddHandler(Mouse.QueryCursorEvent, new QueryCursorEventHandler((_, e) =>
        {
            if (!IsDragging(box) && HitEdge(box, e.GetPosition(box)) is null) return;
            e.Cursor = Cursors.SizeWE;
            e.Handled = true;
        }), handledEventsToo: true);

        box.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (!TryBegin(box, e.GetPosition(box))) return;
            box.CaptureMouse();
            e.Handled = true;                          // 글자 선택이 시작되지 않게 [실측 — 계획 검토 R-6]
        };
        box.PreviewMouseMove += (_, e) =>
        {
            if (!IsDragging(box)) return;
            MoveTo(box, e.GetPosition(box));
            e.Handled = true;
        };
        box.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (!IsDragging(box)) return;
            End(box);
            if (box.IsMouseCaptured) box.ReleaseMouseCapture();
            e.Handled = true;
        };
        // 창 밖 · 다른 창으로 놓쳐도 놓은 것으로 끝낸다
        box.LostMouseCapture += (_, _) => { if (IsDragging(box)) End(box); };
    }

    public static bool IsDragging(RichTextBox box) => box.GetValue(DragProperty) is Drag;

    // ── 계산 ────────────────────────────────────────────────────────────────

    /// 끄는 순간 그려진 본문 폭 — 표가 차지할 수 있는 폭(왼쪽 선 1px 뺌). `1*` 열은 이것을 나눠 갖는다 [실측 — R-4].
    public static double LayoutWidth(RichTextBox box)
    {
        static double Side(double value) => double.IsNaN(value) ? 0 : value;
        var page = box.Document.PagePadding;
        return box.ViewportWidth - Side(page.Left) - Side(page.Right) - 1;
    }

    /// 열마다 지금 그려진 폭(px). 고정 폭은 그 값, `1*` 열은 남은 폭을 별 비율로 나눈 값.
    public static double[] ColumnWidths(RichTextBox box, Table table)
    {
        var columns = table.Columns;
        var fixedTotal = columns.Where(c => c.Width.IsAbsolute).Sum(c => c.Width.Value);
        var stars = columns.Where(c => c.Width.IsStar).Sum(c => c.Width.Value);
        var rest = Math.Max(0, LayoutWidth(box) - fixedTotal);
        return [.. columns.Select(c => c.Width.IsAbsolute ? c.Width.Value : c.Width.IsStar && stars > 0 ? rest * c.Width.Value / stars : MinColumnWidth)];
    }

    /// <summary>
    /// 문서 안의 표 경계들. 아직 안 그려진 표는 건너뛴다. 왼쪽 위 칸(칸 지도 (0,0) — 합친 칸이어도 된다, D-174)의 글자 위치에서 표 왼쪽을 구하고,
    /// 경계는 열 폭을 더해 간다 — 칸이 아니라 열 정의에서 나오므로 합친 칸이 걸쳐도 같다.
    /// </summary>
    public static IReadOnlyList<Edge> Edges(RichTextBox box)
    {
        var edges = new List<Edge>();
        foreach (var table in Tables(box.Document.Blocks))
        {
            var grid = TableGrid.Build(table);
            if (grid.RowCount == 0 || table.Columns.Count == 0 || grid.At(0, 0) is not { } first) continue;

            var start = Rect(first.ContentStart, LogicalDirection.Forward);
            if (start.IsEmpty) continue;

            // 표 왼쪽 = 첫 칸 글자 x − 칸 여백 − 표 왼쪽 선 1 · 위 = 첫 칸 글자 y − 칸 여백 − 표 위 선 1 [실측 — R-4]
            // 아래 = 마지막 행까지 내려오는 칸들의 글자 끝 중 가장 아래 + 칸 여백 + 선
            var left = start.X - first.Padding.Left - 1;
            var top = start.Y - first.Padding.Top - 1;
            var lastRow = grid.RowCount - 1;
            var bottom = grid.Cells.Where(c => grid.Corner(c).Row == lastRow).Select(c => (Rect: Rect(c.ContentEnd, LogicalDirection.Backward), c.Padding.Bottom))
                             .Where(x => !x.Rect.IsEmpty).Select(x => x.Rect.Bottom + x.Bottom + 1).DefaultIfEmpty(start.Bottom).Max();

            var widths = ColumnWidths(box, table);
            var x = left + 1;
            for (var i = 0; i < widths.Length; i++)
            {
                x += widths[i];
                edges.Add(new Edge(table, i, x, top, bottom));
            }
        }
        return edges;
    }

    public static Edge? HitEdge(RichTextBox box, Point point)
    {
        if (box.IsReadOnly) return null;
        return Edges(box).Where(e => point.Y >= e.Top && point.Y <= e.Bottom && Math.Abs(point.X - e.X) <= Grip)
                         .OrderBy(e => Math.Abs(point.X - e.X)).FirstOrDefault();
    }

    /// <summary>
    /// 끈 뒤의 열 폭(정수 px). 열 경계는 그 열만, 바깥선은 모두 같은 비율로. 열은 24px 이상, 합계는 max 이하 —
    /// 이미 max 보다 넓은 표(창을 줄였다)는 지금 합계까지만 허용한다(넓히지는 못하고 줄일 수는 있다).
    /// </summary>
    public static double[] Resized(IReadOnlyList<double> start, int index, double delta, double max)
    {
        var total = start.Sum();
        var limit = Math.Max(max, total);

        if (index < start.Count - 1)
        {
            var widths = start.ToArray();
            widths[index] = Math.Clamp(start[index] + delta, MinColumnWidth, Math.Max(MinColumnWidth, start[index] + limit - total));
            return [.. widths.Select(w => Math.Round(w))];
        }

        var wanted = Math.Clamp(total + delta, MinColumnWidth * start.Count, limit);
        var scale = wanted / total;
        return [.. start.Select(w => Math.Round(Math.Max(MinColumnWidth, w * scale)))];
    }

    // ── 끌기 (시험은 이 메서드를 좌표로 직접 부른다) ─────────────────────────

    public static bool TryBegin(RichTextBox box, Point point)
    {
        if (IsDragging(box) || HitEdge(box, point) is not { } edge) return false;

        var widths = ColumnWidths(box, edge.Table);
        var original = edge.Table.Columns.Select(c => c.Width).ToArray();
        // 시작 폭은 지금 실제 폭(px) — 움직이면 모든 열이 px 로 정해져 다른 열이 튀지 않는다. 놓을 때 원래 값으로 되돌린 뒤 갈아 끼운다
        box.SetValue(DragProperty, new Drag(edge.Table, edge.Index, point.X, widths, original, Math.Max(LayoutWidth(box), widths.Sum())));
        return true;
    }

    public static void MoveTo(RichTextBox box, Point point)
    {
        if (box.GetValue(DragProperty) is not Drag drag) return;
        drag.Current = Resized(drag.StartWidths, drag.Index, point.X - drag.StartX, drag.Max);
        Apply(drag.Table, drag.Current);
    }

    /// <summary>
    /// 놓기. 원래 폭으로 되돌리고, 폭이 바뀌었으면 새 폭을 단 복제 표로 한 번에 갈아 끼운다(실행취소 한 번).
    /// 표가 그새 문서에서 빠졌으면(다시 열기 · 테마 바꾸기) 버린다. 복제가 실패하면 원래대로 두고 아무것도 바꾸지 않는다.
    /// </summary>
    public static void End(RichTextBox box)
    {
        if (box.GetValue(DragProperty) is not Drag drag) return;
        box.ClearValue(DragProperty);

        var table = drag.Table;
        for (var i = 0; i < table.Columns.Count && i < drag.Original.Length; i++) table.Columns[i].Width = drag.Original[i];

        var changed = drag.Current.Where((w, i) => Math.Abs(w - drag.StartWidths[i]) >= 0.5).Any();
        if (!changed || !IsIn(box, table)) return;

        Table clone;
        try
        {
            clone = (Table)XamlReader.Parse(XamlWriter.Save(table));
        }
        catch (Exception ex)
        {
            AppLog.Error("table-resize", null, ex);
            return;
        }
        Apply(clone, drag.Current);

        var caret = CellIndex(box, table);
        box.BeginChange();
        try
        {
            table.SiblingBlocks!.InsertAfter(table, clone);
            table.SiblingBlocks!.Remove(table);
            if (caret is var (row, column) && CellAt(clone, row, column) is { } cell)
                box.CaretPosition = cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        }
        finally
        {
            box.EndChange();
        }
    }

    // ── 도움 ────────────────────────────────────────────────────────────────

    private static void Apply(Table table, IReadOnlyList<double> widths)
    {
        for (var i = 0; i < table.Columns.Count && i < widths.Count; i++) table.Columns[i].Width = new GridLength(widths[i]);
    }

    private static Rect Rect(TextPointer position, LogicalDirection direction)
        => position.GetInsertionPosition(direction) is { } at ? at.GetCharacterRect(direction) : System.Windows.Rect.Empty;

    private static IEnumerable<Table> Tables(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Table table) yield return table;
            else if (block is Section section)
                foreach (var inner in Tables(section.Blocks)) yield return inner;
        }
    }

    private static bool IsIn(RichTextBox box, Table table)
    {
        for (DependencyObject? at = table; at is not null; at = LogicalTreeHelper.GetParent(at))
            if (ReferenceEquals(at, box.Document)) return true;
        return false;
    }

    /// 커서가 이 표의 칸에 있으면 그 (행, 열) — 갈아 끼운 표의 같은 칸에 커서를 다시 둔다. 다른 곳이면 null(커서를 건드리지 않는다).
    private static (int Row, int Column)? CellIndex(RichTextBox box, Table table)
    {
        if (RichTable.CellAt(box) is not { } cell || cell.Parent is not TableRow row || row.Parent is not TableRowGroup group
            || !ReferenceEquals(group.Parent, table)) return null;
        var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
        return (rows.IndexOf(row), row.Cells.IndexOf(cell));
    }

    private static TableCell? CellAt(Table table, int row, int column)
    {
        var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();
        return row >= 0 && row < rows.Count && column >= 0 && column < rows[row].Cells.Count ? rows[row].Cells[column] : null;
    }
}
