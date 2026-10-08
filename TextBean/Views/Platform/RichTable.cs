using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace TextBean.Views.Platform;

/// <summary>
/// 서식 본문의 표 (D-131 ~ D-136). WPF RichTextBox 는 표를 담고 그리기만 하고, 넣기 · 행/열 · 지우기는 기본 기능이 없다.
/// 표만 골라 지우면 표는 남고 칸 글자만 비워진다 [실측] — 그래서 「표 지우기」가 따로 있다.
/// 모든 조작은 BeginChange/EndChange 로 묶는다 — 실행취소 한 번에 되돌아간다 [실측].
/// </summary>
public static class RichTable
{
    public const int MaxPick = 8;

    /// 매개변수: "행x열" (예: "3x4"). 1 ~ MaxPick.
    public static readonly RoutedUICommand InsertTable = new("표 넣기", nameof(InsertTable), typeof(RichTable));
    public static readonly RoutedUICommand InsertRowAbove = new("위에 행 넣기", nameof(InsertRowAbove), typeof(RichTable));
    public static readonly RoutedUICommand InsertRowBelow = new("아래에 행 넣기", nameof(InsertRowBelow), typeof(RichTable));
    public static readonly RoutedUICommand InsertColumnLeft = new("왼쪽에 열 넣기", nameof(InsertColumnLeft), typeof(RichTable));
    public static readonly RoutedUICommand InsertColumnRight = new("오른쪽에 열 넣기", nameof(InsertColumnRight), typeof(RichTable));
    public static readonly RoutedUICommand DeleteRow = new("행 지우기", nameof(DeleteRow), typeof(RichTable));
    public static readonly RoutedUICommand DeleteColumn = new("열 지우기", nameof(DeleteColumn), typeof(RichTable));
    public static readonly RoutedUICommand DeleteTable = new("표 지우기", nameof(DeleteTable), typeof(RichTable));
    public static readonly RoutedUICommand NextCell = new("다음 칸", nameof(NextCell), typeof(RichTable));
    public static readonly RoutedUICommand PreviousCell = new("이전 칸", nameof(PreviousCell), typeof(RichTable));

    /// 고른 칸들의 글자 가로 정렬 (D-172). 매개변수 "Left" · "Center" · "Right". 우클릭 메뉴에서만(사용자 판정).
    public static readonly RoutedUICommand AlignCells = new("칸 정렬", nameof(AlignCells), typeof(RichTable));

    /// 고른 칸들을 하나로 (D-175). 글자는 읽는 순서대로 이어 붙인다(사용자 판정).
    public static readonly RoutedUICommand MergeCells = new("칸 합치기", nameof(MergeCells), typeof(RichTable));

    /// 합친 칸을 원래 칸들로 푼다 (D-175). 합친 적 없는 칸은 나누지 않는다(사용자 판정).
    public static readonly RoutedUICommand SplitCell = new("칸 나누기", nameof(SplitCell), typeof(RichTable));

    /// 우클릭 메뉴에서 표 안일 때만 보이는 항목의 Tag.
    public const string MenuTag = "table";

    /// 칸 선 — 지금 테마에서 보이는 색 (D-162). 문서에는 저장 때 저장 색으로 담긴다.
    private static Brush Line => Frozen(DocumentColors.Shown(DocumentColors.TableLine, AppTheme.IsDark));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static void Attach(RichTextBox box)
    {
        box.CommandBindings.Add(new CommandBinding(InsertTable, (_, e) =>
        {
            if (TryParseSize(e.Parameter, out var rows, out var columns)) Insert(box, rows, columns);
        }, (_, e) => e.CanExecute = !box.IsReadOnly && TryParseSize(e.Parameter, out int _, out int _)));

        Bind(box, InsertRowAbove, cell => InsertRow(box, cell, below: false));
        Bind(box, InsertRowBelow, cell => InsertRow(box, cell, below: true));
        Bind(box, InsertColumnLeft, cell => InsertColumn(box, cell, right: false));
        Bind(box, InsertColumnRight, cell => InsertColumn(box, cell, right: true));
        Bind(box, DeleteRow, cell => RemoveRows(box, cell));
        Bind(box, DeleteColumn, cell => RemoveColumns(box, cell));

        box.CommandBindings.Add(new CommandBinding(MergeCells,
            (_, _) => { if (CellAt(box) is { } cell) Edit(box, () => Merge(box, cell)); },
            (_, e) => e.CanExecute = !box.IsReadOnly && CellAt(box) is { } cell && SelectedArea(box, cell) is var (grid, area) && grid.CellsIn(area).Skip(1).Any()));
        box.CommandBindings.Add(new CommandBinding(SplitCell,
            (_, _) => { if (CellAt(box) is { } cell) Edit(box, () => Split(box, cell)); },
            (_, e) => e.CanExecute = !box.IsReadOnly && CellAt(box) is { RowSpan: > 1 } or { ColumnSpan: > 1 }));
        Bind(box, DeleteTable, cell => Remove(box, TableOf(cell)));
        Bind(box, NextCell, cell => Move(box, cell, forward: true));
        Bind(box, PreviousCell, cell => Move(box, cell, forward: false));

        box.CommandBindings.Add(new CommandBinding(AlignCells,
            (_, e) => { if (CellAt(box) is { } cell && ParseAlignment(e.Parameter) is { } alignment) Edit(box, () => Align(box, cell, alignment)); },
            (_, e) => e.CanExecute = !box.IsReadOnly && CellAt(box) is not null && ParseAlignment(e.Parameter) is not null));

        // 열 너비 끌기 · 표 전체 줄이기 (D-170 · D-171)
        TableColumnResizer.Attach(box);

        // Tab · Shift+Tab 은 표 안에서만 가로챈다. 표 밖이면 손대지 않아 WPF 기본(탭 글자)으로 간다 (D-133).
        // 키 바인딩으로 두면 「안 됨」인 명령이 키를 삼키는지가 화면 밖에서 판정되지 않는다 — 판정이 필요 없는 쪽을 쓴다.
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Tab || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;

            var command = Keyboard.Modifiers == ModifierKeys.Shift ? PreviousCell : NextCell;
            if (!command.CanExecute(null, box)) return;

            command.Execute(null, box);
            e.Handled = true;
        };
    }

    private static void Bind(RichTextBox box, RoutedCommand command, Action<TableCell> run)
        => box.CommandBindings.Add(new CommandBinding(command,
            (_, _) => { if (CellAt(box) is { } cell) Edit(box, () => run(cell)); },
            (_, e) => e.CanExecute = !box.IsReadOnly && CellAt(box) is not null));

    private static void Edit(RichTextBox box, Action change)
    {
        box.BeginChange();
        try { change(); }
        finally { box.EndChange(); }
    }

    public static bool TryParseSize(object? parameter, out int rows, out int columns)
    {
        rows = columns = 0;
        if (parameter is not string text || text.Split('x') is not [var r, var c]) return false;
        return int.TryParse(r, out rows) && int.TryParse(c, out columns)
               && rows is >= 1 and <= MaxPick && columns is >= 1 and <= MaxPick;
    }

    /// 선택 시작이 든 칸. 표 밖이면 null.
    public static TableCell? CellAt(RichTextBox box) => CellOf(box.Selection.Start);

    private static TableCell? CellOf(TextPointer position)
    {
        for (DependencyObject? at = position.Parent; at is not null; at = (at as TextElement)?.Parent)
            if (at is TableCell cell) return cell;
        return null;
    }

    /// 우클릭 메뉴를 열 때 — 표 항목은 표 안일 때만 보인다. 표 항목 앞의 구분선도 같이.
    public static void UpdateMenu(RichTextBox box, ContextMenu menu)
    {
        var inTable = !box.IsReadOnly && CellAt(box) is not null;
        foreach (var item in menu.Items.OfType<FrameworkElement>().Where(i => Equals(i.Tag, MenuTag)))
            item.Visibility = inTable ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 만들기 · 넣기 ────────────────────────────────────────────────────────

    /// 같은 폭 열 · 테두리 1px · 칸 여백 6,3 (D-136). 테두리는 칸에 직접 적는다 — XamlPackage 에 그대로 남는다 [실측].
    public static Table Create(int rows, int columns)
    {
        var table = new Table { CellSpacing = 0, BorderBrush = Line, BorderThickness = new Thickness(1, 1, 0, 0) };
        for (var c = 0; c < columns; c++) table.Columns.Add(NewColumn());

        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (var r = 0; r < rows; r++) group.Rows.Add(NewRow(columns));
        return table;
    }

    private static TableColumn NewColumn() => new() { Width = new GridLength(1, GridUnitType.Star) };

    private static TableRow NewRow(int columns)
    {
        var row = new TableRow();
        for (var c = 0; c < columns; c++) row.Cells.Add(NewCell());
        return row;
    }

    private static TableCell NewCell() => new(new Paragraph())
    {
        BorderBrush = Line,
        BorderThickness = new Thickness(0, 0, 1, 1),
        Padding = new Thickness(6, 3, 6, 3)
    };

    /// <summary>
    /// 캐럿 문단이 비어 있으면 그 앞에, 아니면 그 뒤에. 캐럿이 표 안이면 그 표 뒤에 (D-136).
    /// 표 뒤에는 늘 문단을 둔다 — 문서 끝이 표면 표 뒤에 캐럿을 둘 곳이 없다 [실측].
    /// </summary>
    private static void Insert(RichTextBox box, int rows, int columns)
    {
        var table = Create(rows, columns);
        Edit(box, () =>
        {
            if (CellAt(box) is { } cell)
            {
                var host = TableOf(cell);
                host.SiblingBlocks!.InsertAfter(host, table);
            }
            else if (box.Selection.Start.Paragraph is { } paragraph)
            {
                if (new TextRange(paragraph.ContentStart, paragraph.ContentEnd).IsEmpty) paragraph.SiblingBlocks!.InsertBefore(paragraph, table);
                else paragraph.SiblingBlocks!.InsertAfter(paragraph, table);
            }
            else
            {
                box.Document.Blocks.Add(table);
            }

            if (table.NextBlock is null or Table) table.SiblingBlocks!.InsertAfter(table, new Paragraph());
            Focus(box, FirstCell(table));
        });
    }

    // ── 행 · 열 ──────────────────────────────────────────────────────────────

    private static Table TableOf(TableCell cell) => (Table)((TableRowGroup)((TableRow)cell.Parent).Parent).Parent;

    private static List<TableRow> Rows(Table table) => [.. table.RowGroups.SelectMany(g => g.Rows)];

    private static List<TableCell> Cells(Table table) => [.. Rows(table).SelectMany(r => r.Cells)];

    private static TableCell FirstCell(Table table) => Cells(table)[0];

    private static void Focus(RichTextBox box, TableCell cell) => box.CaretPosition = cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward);

    /// <summary>
    /// 칸의 위(아래)에 행을 넣는다 — 칸 지도 위에서 (D-174). 넣는 자리를 위아래로 걸친 합친 칸은 한 칸 늘리고
    /// 그 칸이 덮는 열에는 새 칸을 넣지 않는다(워드 방식, D-175). 행만 바뀌므로 그 자리에서 고친다(실행취소에 잡힌다 [실측]).
    /// </summary>
    private static void InsertRow(RichTextBox box, TableCell cell, bool below)
    {
        var table = TableOf(cell);
        var grid = TableGrid.Build(table);
        var (top, column) = grid.Origin(cell);
        var at = below ? grid.Corner(cell).Row + 1 : top;

        var added = new TableRow();
        for (var c = 0; c < grid.ColumnCount;)
        {
            if (at > 0 && at < grid.RowCount && grid.At(at - 1, c) is { } over && ReferenceEquals(over, grid.At(at, c)))
            {
                over.RowSpan = Math.Max(1, over.RowSpan) + 1;
                c = grid.Corner(over).Column + 1;
                continue;
            }
            added.Cells.Add(NewCell());
            c++;
        }

        var reference = grid.Rows[Math.Min(at, grid.RowCount - 1)];
        var group = (TableRowGroup)reference.Parent;
        group.Rows.Insert(group.Rows.IndexOf(reference) + (at >= grid.RowCount ? 1 : 0), added);
        if (TableGrid.Build(table).At(at, column) is { } focus) Focus(box, focus);
    }

    /// <summary>
    /// 칸의 왼쪽(오른쪽)에 열을 넣는다 — 칸 지도 위에서 (D-174). 넣는 자리를 좌우로 걸친 합친 칸은 한 칸 늘린다(D-175).
    /// 열 정의가 바뀌므로 표를 통째로 갈아 끼운다(D-176).
    /// </summary>
    private static void InsertColumn(RichTextBox box, TableCell cell, bool right)
    {
        var table = TableOf(cell);
        var original = TableGrid.Build(table);
        var (row, left) = original.Origin(cell);
        var at = right ? original.Corner(cell).Column + 1 : left;

        Swap(box, table, clone =>
        {
            var grid = TableGrid.Build(clone);
            var grow = new HashSet<TableCell>();
            var inserts = new List<(TableRow Row, int Index)>();
            for (var r = 0; r < grid.RowCount; r++)
            {
                if (at > 0 && at < grid.ColumnCount && grid.At(r, at - 1) is { } over && ReferenceEquals(over, grid.At(r, at)))
                {
                    grow.Add(over);
                    continue;
                }
                inserts.Add((grid.Rows[r], grid.InsertIndex(r, at)));
            }

            var column = NewColumnBeside(box, clone, at);
            foreach (var over in grow) over.ColumnSpan = Math.Max(1, over.ColumnSpan) + 1;
            foreach (var (target, index) in inserts) target.Cells.Insert(Math.Min(index, target.Cells.Count), NewCell());
            clone.Columns.Insert(Math.Min(at, clone.Columns.Count), column);
            return (TableGrid.Build(clone).At(row, at), false);
        });
    }

    /// <summary>
    /// 새 열 정의. 같은 폭 나눔 표면 `1*`. 폭을 정한 표(D-170)면 옆 열과 같은 폭 — 그래서 본문보다 넓어지면 모든 열을 같은 비율로 줄인다
    /// (넘으면 오른쪽이 잘린다 [실측]).
    /// </summary>
    private static TableColumn NewColumnBeside(RichTextBox box, Table table, int at)
    {
        if (table.Columns.Count == 0 || table.Columns.All(c => c.Width.IsStar)) return NewColumn();

        var widths = TableColumnResizer.ColumnWidths(box, table);
        var added = widths[Math.Clamp(at - 1, 0, widths.Length - 1)];
        var limit = TableColumnResizer.LayoutWidth(box);
        var total = widths.Sum() + added;
        if (limit > 0 && total > limit)
        {
            var scale = limit / total;
            for (var i = 0; i < table.Columns.Count; i++)
                table.Columns[i].Width = new GridLength(Math.Max(TableColumnResizer.MinColumnWidth, Math.Round(widths[i] * scale)));
            added = Math.Max(TableColumnResizer.MinColumnWidth, Math.Round(added * scale));
        }
        return new TableColumn { Width = new GridLength(added) };
    }

    /// <summary>
    /// 표를 복제해 고치고 한 번에 갈아 끼운다 (D-176). 열 정의(TableColumn)는 실행취소에 안 잡혀 [실측 — P2],
    /// 그 자리에서 열을 넣고 지우면 되돌린 뒤 열 수와 칸 수가 어긋났다 [실측 — 시험: 열 넣기 → 되돌리기 = 열 3 · 칸 2].
    /// 통째로 갈아 끼우면 실행취소 한 번에 옛 표가 그대로 돌아온다 · 칸 안 그림도 산다 [실측 — R-1]. 고친 뒤 커서를 둘 칸(끝이면 true)을 돌려준다.
    /// </summary>
    private static void Swap(RichTextBox box, Table table, Func<Table, (TableCell? Cell, bool AtEnd)> change)
    {
        var clone = (Table)XamlReader.Parse(XamlWriter.Save(table));
        var (focus, atEnd) = change(clone);
        table.SiblingBlocks!.InsertAfter(table, clone);
        table.SiblingBlocks!.Remove(table);
        if (focus is null) return;
        box.CaretPosition = atEnd ? focus.ContentEnd.GetInsertionPosition(LogicalDirection.Backward) : focus.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
    }

    /// <summary>
    /// 선택이 덮는 칸 직사각형 (D-138 · D-174). 선택이 비었거나 끝이 표 밖이면 시작 칸 하나(합친 칸이면 그 칸 전체).
    /// 칸 선택은 직사각형이다 — 문서 순서로 겹친 칸을 다 세면 (1,1)~(2,2) 에 (2,0)이 끼어든다. 두 모서리만 쓰고, 걸친 합친 칸까지 넓힌다.
    /// 여러 칸에 걸친 선택은 WPF 가 칸 직사각형으로 바꾸고 끝을 **다음 칸의 시작**에 둔다 [실측 — D-173]. 끝 바로 앞 글자의 칸을 마지막 모서리로 쓴다.
    /// </summary>
    private static (TableGrid Grid, TableGrid.Area Area) SelectedArea(RichTextBox box, TableCell start)
    {
        var table = TableOf(start);
        var grid = TableGrid.Build(table);
        var selection = box.Selection;
        if (selection.IsEmpty || selection.End.CompareTo(table.ContentEnd) > 0) return (grid, grid.Span(start, start));

        var first = CellOf(selection.Start) is { } s && ReferenceEquals(TableOf(s), table) ? s : start;
        var last = EndCell(selection.End, table) ?? first;

        // 빈 칸까지 끌면 끝이 그 빈 칸의 시작에 놓여 위 규칙이 앞 칸으로 돌린다 [실측 — 렌더: 「접속 정보」 + 빈 칸 합치기가 안 됐다].
        // 한 칸으로 줄어들 때만, 끝이 다른 빈 칸 시작이면 그 칸까지 넣는다 — 여러 칸 직사각형(D-173)은 건드리지 않는다
        if (ReferenceEquals(first, last) && CellOf(selection.End) is { } landed && !ReferenceEquals(landed, first)
            && ReferenceEquals(TableOf(landed), table) && landed.Blocks.All(IsBlank))
            last = landed;

        return (grid, grid.Span(first, last));
    }

    private static TableCell? EndCell(TextPointer end, Table table)
    {
        var cell = CellOf(end);
        if (cell is null || end.CompareTo(cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward)) <= 0)
            cell = end.GetNextInsertionPosition(LogicalDirection.Backward) is { } before ? CellOf(before) : null;
        return cell is not null && ReferenceEquals(TableOf(cell), table) ? cell : null;
    }

    // ── 칸 안 정렬 (D-172) ───────────────────────────────────────────────────

    private static TextAlignment? ParseAlignment(object? parameter) => parameter switch
    {
        "Left" => TextAlignment.Left,
        "Center" => TextAlignment.Center,
        "Right" => TextAlignment.Right,
        _ => null,
    };

    /// <summary>
    /// 선택 직사각형(두 모서리 — D-138) 안 칸들의 문단에 정렬을 직접 넣는다. 한 번의 변경이라 실행취소 한 번 [실측 — 시험].
    /// </summary>
    private static void Align(RichTextBox box, TableCell start, TextAlignment alignment)
    {
        var (grid, area) = SelectedArea(box, start);
        foreach (var cell in grid.CellsIn(area))
            foreach (var paragraph in Paragraphs(cell.Blocks)) paragraph.TextAlignment = alignment;
    }

    private static IEnumerable<Paragraph> Paragraphs(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph) yield return paragraph;
            else if (block is Section section)
                foreach (var inner in Paragraphs(section.Blocks)) yield return inner;
        }
    }

    /// <summary>
    /// 선택이 걸친 행을 모두 지운다 (D-138 · D-174). 모든 행이 걸치면 표를 지운다 — 빈 표를 남기지 않는다 (D-132).
    /// 지우는 행에 걸친 합친 칸은 남기고 줄인다(사용자 판정 Q-4 A). 지우는 행에서 시작해 아래로 이어진 칸은 남은 첫 행으로 내린다 — 글자를 잃지 않는다.
    /// 행만 바뀌므로 그 자리에서 고친다(실행취소에 잡힌다).
    /// </summary>
    private static void RemoveRows(RichTextBox box, TableCell start)
    {
        var table = TableOf(start);
        var (grid, area) = SelectedArea(box, start);
        if (area.Top == 0 && area.Bottom == grid.RowCount - 1)
        {
            Remove(box, table);
            return;
        }

        var column = grid.Origin(start).Column;
        var moves = new List<(TableCell Cell, int Column, int Span)>();
        foreach (var cell in grid.Cells.ToList())
        {
            var (top, left) = grid.Origin(cell);
            var bottom = grid.Corner(cell).Row;
            if (bottom < area.Top || top > area.Bottom) continue;

            if (top >= area.Top && bottom > area.Bottom) moves.Add((cell, left, bottom - area.Bottom));
            else if (top < area.Top) cell.RowSpan = Math.Max(1, cell.RowSpan) - (Math.Min(bottom, area.Bottom) - area.Top + 1);
        }

        // 아래 행으로 내리기 — 오른쪽 칸부터 넣어 앞서 넣은 칸이 번호를 밀지 않게.
        // 내릴 칸이 있으면 아래 행이 반드시 있다(그 칸이 지우는 행 아래까지 이어진다). 마지막 행을 지울 때는 내릴 칸이 없다
        foreach (var (cell, left, span) in moves.OrderByDescending(m => m.Column))
        {
            var below = grid.Rows[area.Bottom + 1];
            var index = grid.InsertIndex(area.Bottom + 1, left);
            ((TableRow)cell.Parent).Cells.Remove(cell);
            cell.RowSpan = span;
            below.Cells.Insert(Math.Min(index, below.Cells.Count), cell);
        }

        for (var r = area.Bottom; r >= area.Top; r--) ((TableRowGroup)grid.Rows[r].Parent).Rows.Remove(grid.Rows[r]);

        var after = TableGrid.Build(table);
        if (after.At(Math.Min(area.Top, after.RowCount - 1), Math.Min(column, after.ColumnCount - 1)) is { } focus) Focus(box, focus);
    }

    /// <summary>
    /// 선택이 걸친 열을 모두 지운다 (D-138 · D-174). 모든 열이 걸치면 표를 지운다 (D-132).
    /// 지우는 열에 걸친 합친 칸은 남기고 줄인다(Q-4 A). 열 정의가 바뀌므로 표를 통째로 갈아 끼운다(D-176).
    /// </summary>
    private static void RemoveColumns(RichTextBox box, TableCell start)
    {
        var table = TableOf(start);
        var (grid, area) = SelectedArea(box, start);
        if (area.Left == 0 && area.Right == grid.ColumnCount - 1)
        {
            Remove(box, table);
            return;
        }

        var row = grid.Origin(start).Row;
        Swap(box, table, clone =>
        {
            var map = TableGrid.Build(clone);
            foreach (var cell in map.Cells.ToList())
            {
                var left = map.Origin(cell).Column;
                var right = map.Corner(cell).Column;
                if (right < area.Left || left > area.Right) continue;

                var overlap = Math.Min(right, area.Right) - Math.Max(left, area.Left) + 1;
                if (overlap >= right - left + 1) ((TableRow)cell.Parent).Cells.Remove(cell);
                else cell.ColumnSpan = right - left + 1 - overlap;
            }
            for (var c = Math.Min(area.Right, clone.Columns.Count - 1); c >= area.Left; c--) clone.Columns.RemoveAt(c);

            var after = TableGrid.Build(clone);
            return (after.At(Math.Min(row, after.RowCount - 1), Math.Min(area.Left, after.ColumnCount - 1)), false);
        });
    }

    // ── 칸 합치기 · 풀기 (D-175) ─────────────────────────────────────────────

    /// <summary>
    /// 고른 직사각형(걸친 합친 칸까지 넓힘)을 왼쪽 위 칸 하나로. 다른 칸의 글자는 읽는 순서대로 이어 붙인다(사용자 판정) — 빈 문단은 버린다.
    /// 칸만 바뀌므로 그 자리에서 고친다 — 실행취소 한 번 · 저장 왕복 [실측 — P3].
    /// </summary>
    private static void Merge(RichTextBox box, TableCell start)
    {
        var (grid, area) = SelectedArea(box, start);
        var cells = grid.CellsIn(area).ToList();
        if (cells.Count < 2 || grid.At(area.Top, area.Left) is not { } target) return;

        var moved = new List<Block>();
        foreach (var other in cells.Where(c => !ReferenceEquals(c, target)))
        {
            moved.AddRange(other.Blocks.Where(b => !IsBlank(b)));
            other.Blocks.Clear();
            ((TableRow)other.Parent).Cells.Remove(other);
        }

        if (moved.Count > 0 && target.Blocks.All(IsBlank)) target.Blocks.Clear();
        target.Blocks.AddRange(moved);
        if (target.Blocks.Count == 0) target.Blocks.Add(new Paragraph());

        target.ColumnSpan = area.Right - area.Left + 1;
        target.RowSpan = area.Bottom - area.Top + 1;
        Focus(box, target);
    }

    /// 합친 칸을 1×1로 되돌리고 덮였던 자리에 빈 칸을 넣는다. 글자는 왼쪽 위 칸에 남는다(사용자 판정).
    private static void Split(RichTextBox box, TableCell cell)
    {
        var grid = TableGrid.Build(TableOf(cell));
        var (top, left) = grid.Origin(cell);
        var (bottom, right) = grid.Corner(cell);

        cell.RowSpan = 1;
        cell.ColumnSpan = 1;
        for (var r = top; r <= bottom; r++)
        {
            // 오른쪽부터 같은 번호에 넣는다 — 먼저 넣은 칸이 뒤로 밀려 제자리가 된다
            for (var c = right; c >= left; c--)
            {
                if (r == top && c == left) continue;
                var row = grid.Rows[r];
                row.Cells.Insert(Math.Min(grid.InsertIndex(r, c), row.Cells.Count), NewCell());
            }
        }
        Focus(box, cell);
    }

    /// 글자도 그림도 없는 문단.
    private static bool IsBlank(Block block)
        => block is Paragraph paragraph && !paragraph.Inlines.OfType<InlineUIContainer>().Any()
           && new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim().Length == 0;

    /// 표를 지우고 캐럿을 그 자리 다음(없으면 앞) 문단에 둔다. 문서가 비면 빈 문단 하나.
    private static void Remove(RichTextBox box, Table table)
    {
        var next = table.NextBlock ?? table.PreviousBlock;
        table.SiblingBlocks!.Remove(table);

        if (box.Document.Blocks.Count == 0) box.Document.Blocks.Add(next = new Paragraph());
        if (next is not null) box.CaretPosition = next.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
    }

    // ── 칸 안 붙여넣기 (D-137) ───────────────────────────────────────────────

    /// <summary>
    /// 앱 안 서식을 칸 안에 붙일 때. 그대로 두면 표가 든 내용은 표 안에 표가 된다 [실측 — 사용자 화면].
    /// <list type="bullet">
    /// <item>칸 밖이거나 표가 없는 서식 → NotHandled(지금처럼 서식째)</item>
    /// <item>표 하나만 든 내용 → 그 칸부터 덮어쓴다(모자라면 행 · 열을 늘린다) → Overwritten</item>
    /// <item>표 + 글자 · 표 둘 이상 → 칸에 나눌 기준이 없다 → AsText(글자로)</item>
    /// </list>
    /// </summary>
    public static PasteIntoCell Paste(RichTextBox box, IDataObject data)
    {
        if (box.IsReadOnly || CellAt(box) is not { } target) return PasteIntoCell.NotHandled;
        if (data.GetData(DataFormats.XamlPackage) is not System.IO.Stream { CanSeek: true } stream) return PasteIntoCell.NotHandled;

        FlowDocument pasted;
        var position = stream.Position;
        try
        {
            stream.Position = 0;
            pasted = new FlowDocument();
            new TextRange(pasted.ContentStart, pasted.ContentEnd).Load(stream, DataFormats.XamlPackage);
        }
        catch (Exception)
        {
            return PasteIntoCell.NotHandled;      // 못 읽는 서식은 WPF 기본 붙여넣기에 맡긴다 — 거기서도 못 읽으면 아무것도 안 들어간다
        }
        finally
        {
            stream.Position = position;           // 기본 붙여넣기가 같은 스트림을 다시 읽는다
        }

        RichTextMap.ClearFonts(pasted);                // 옮기는 블록이 상대 주소 글꼴을 들고 칸에 들어가지 않게 (D-154)
        DocumentColors.Prepare(pasted, AppTheme.IsDark);   // 복사본은 저장 색이다 — 지금 테마로 (D-162)
        var blocks = Flatten(pasted.Blocks).ToList();
        var tables = blocks.OfType<Table>().ToList();
        if (tables.Count == 0) return PasteIntoCell.NotHandled;

        var textOutside = blocks.Where(b => b is not Table).Any(b => new TextRange(b.ContentStart, b.ContentEnd).Text.Trim().Length > 0);
        if (tables.Count > 1 || textOutside) return PasteIntoCell.AsText;

        // 합친 칸이 있는 표를 붙이거나 합친 칸이 있는 표에 붙이면 글자로 (D-177 — 칸 덮어쓰기는 격자가 고른 표만)
        if (TableGrid.Build(tables[0]).HasMerged || TableGrid.Build(TableOf(target)).HasMerged) return PasteIntoCell.AsText;

        Edit(box, () => Overwrite(box, target, tables[0]));
        return PasteIntoCell.Overwritten;
    }

    /// Section 은 풀어서 본다. 표 안의 블록은 들어가지 않는다(표 하나로 센다).
    private static IEnumerable<Block> Flatten(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Section section)
                foreach (var inner in Flatten(section.Blocks)) yield return inner;
            else
                yield return block;
        }
    }

    /// <summary>
    /// 칸부터 덮어쓴다(모자라면 행 · 열을 늘린다). 합친 칸이 없는 표끼리만 온다(D-177) — 칸 번호 = 열 번호.
    /// 열이 늘 수 있어 표를 통째로 갈아 끼운다(D-176) — 그 자리에서 늘리면 되돌린 뒤 열 정의가 남았다.
    /// </summary>
    private static void Overwrite(RichTextBox box, TableCell target, Table source)
    {
        var table = TableOf(target);
        var startRow = Rows(table).IndexOf((TableRow)target.Parent);
        var startColumn = ((TableRow)target.Parent).Cells.IndexOf(target);
        var sourceRows = Rows(source);

        Swap(box, table, clone =>
        {
            // 모자라면 늘린다 — 표가 네모를 유지하도록 열은 모든 행에 늘린다
            var width = Math.Max(Rows(clone).Max(r => r.Cells.Count), startColumn + sourceRows.Max(r => r.Cells.Count));
            var lastGroup = clone.RowGroups[^1];
            while (Rows(clone).Count < startRow + sourceRows.Count) lastGroup.Rows.Add(NewRow(width));
            foreach (var row in Rows(clone))
                while (row.Cells.Count < width) row.Cells.Add(NewCell());
            while (clone.Columns.Count < width) clone.Columns.Add(NewColumnBeside(box, clone, clone.Columns.Count));

            TableCell? last = null;
            for (var r = 0; r < sourceRows.Count; r++)
            {
                for (var c = 0; c < sourceRows[r].Cells.Count; c++)
                {
                    var from = sourceRows[r].Cells[c];
                    var to = Rows(clone)[startRow + r].Cells[startColumn + c];
                    var moved = from.Blocks.ToList();
                    from.Blocks.Clear();
                    to.Blocks.Clear();
                    to.Blocks.AddRange(moved);
                    if (to.Blocks.Count == 0) to.Blocks.Add(new Paragraph());
                    last = to;
                }
            }
            return (last, true);
        });
    }

    // ── 칸 이동 (D-133) ──────────────────────────────────────────────────────

    /// 다음 · 이전 칸. 마지막 칸에서 다음이면 아래에 행을 넣고 그 첫 칸으로, 첫 칸에서 이전이면 그대로.
    private static void Move(RichTextBox box, TableCell cell, bool forward)
    {
        var cells = Cells(TableOf(cell));
        var at = cells.IndexOf(cell) + (forward ? 1 : -1);

        if (at < 0) return;
        if (at < cells.Count)
        {
            Focus(box, cells[at]);
            return;
        }

        // 새 행은 격자 열 수만큼 — 마지막 칸이 합친 칸이면 그 행의 칸 수가 열 수보다 적다 (D-174)
        var grid = TableGrid.Build(TableOf(cell));
        var lastRow = grid.Rows[^1];
        var added = NewRow(grid.ColumnCount);
        ((TableRowGroup)lastRow.Parent).Rows.Insert(((TableRowGroup)lastRow.Parent).Rows.IndexOf(lastRow) + 1, added);
        Focus(box, added.Cells[0]);
    }
}

public enum PasteIntoCell { NotHandled, Overwritten, AsText }
