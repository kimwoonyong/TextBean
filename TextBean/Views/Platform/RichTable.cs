using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
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

    /// 우클릭 메뉴에서 표 안일 때만 보이는 항목의 Tag.
    public const string MenuTag = "table";

    private static readonly Brush Line = Frozen(Color.FromRgb(0xD3, 0xD1, 0xC7));

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
        Bind(box, DeleteRow, cell => RemoveRow(box, cell));
        Bind(box, DeleteColumn, cell => RemoveColumn(box, cell));
        Bind(box, DeleteTable, cell => Remove(box, TableOf(cell)));
        Bind(box, NextCell, cell => Move(box, cell, forward: true));
        Bind(box, PreviousCell, cell => Move(box, cell, forward: false));

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
    public static TableCell? CellAt(RichTextBox box)
    {
        for (DependencyObject? at = box.Selection.Start.Parent; at is not null; at = (at as TextElement)?.Parent)
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

    private static void InsertRow(RichTextBox box, TableCell cell, bool below)
    {
        var row = (TableRow)cell.Parent;
        var group = (TableRowGroup)row.Parent;
        var added = NewRow(row.Cells.Count);
        group.Rows.Insert(group.Rows.IndexOf(row) + (below ? 1 : 0), added);
        Focus(box, added.Cells[Math.Min(row.Cells.IndexOf(cell), added.Cells.Count - 1)]);
    }

    private static void InsertColumn(RichTextBox box, TableCell cell, bool right)
    {
        var table = TableOf(cell);
        var index = ((TableRow)cell.Parent).Cells.IndexOf(cell) + (right ? 1 : 0);

        foreach (var row in Rows(table)) row.Cells.Insert(Math.Min(index, row.Cells.Count), NewCell());
        table.Columns.Insert(Math.Min(index, table.Columns.Count), NewColumn());
        Focus(box, ((TableRow)cell.Parent).Cells[index]);
    }

    /// 마지막 행을 지우면 표를 지운다 — 빈 표를 남기지 않는다 (D-132).
    private static void RemoveRow(RichTextBox box, TableCell cell)
    {
        var row = (TableRow)cell.Parent;
        var table = TableOf(cell);
        var rows = Rows(table);
        if (rows.Count == 1)
        {
            Remove(box, table);
            return;
        }

        var at = rows.IndexOf(row);
        var column = row.Cells.IndexOf(cell);
        ((TableRowGroup)row.Parent).Rows.Remove(row);

        var neighbour = Rows(table)[Math.Min(at, rows.Count - 2)];
        Focus(box, neighbour.Cells[Math.Min(column, neighbour.Cells.Count - 1)]);
    }

    /// 마지막 열을 지우면 표를 지운다 (D-132).
    private static void RemoveColumn(RichTextBox box, TableCell cell)
    {
        var table = TableOf(cell);
        var row = (TableRow)cell.Parent;
        var index = row.Cells.IndexOf(cell);
        if (row.Cells.Count == 1)
        {
            Remove(box, table);
            return;
        }

        foreach (var each in Rows(table).Where(r => index < r.Cells.Count)) each.Cells.RemoveAt(index);
        if (index < table.Columns.Count) table.Columns.RemoveAt(index);
        Focus(box, row.Cells[Math.Min(index, row.Cells.Count - 1)]);
    }

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

        var blocks = Flatten(pasted.Blocks).ToList();
        var tables = blocks.OfType<Table>().ToList();
        if (tables.Count == 0) return PasteIntoCell.NotHandled;

        var textOutside = blocks.Where(b => b is not Table).Any(b => new TextRange(b.ContentStart, b.ContentEnd).Text.Trim().Length > 0);
        if (tables.Count > 1 || textOutside) return PasteIntoCell.AsText;

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

    private static void Overwrite(RichTextBox box, TableCell target, Table source)
    {
        var table = TableOf(target);
        var startRow = Rows(table).IndexOf((TableRow)target.Parent);
        var startColumn = ((TableRow)target.Parent).Cells.IndexOf(target);
        var sourceRows = Rows(source);

        // 모자라면 늘린다 — 표가 네모를 유지하도록 열은 모든 행에 늘린다
        var width = Math.Max(Rows(table).Max(r => r.Cells.Count), startColumn + sourceRows.Max(r => r.Cells.Count));
        var lastGroup = table.RowGroups[^1];
        while (Rows(table).Count < startRow + sourceRows.Count) lastGroup.Rows.Add(NewRow(width));
        foreach (var row in Rows(table))
            while (row.Cells.Count < width) row.Cells.Add(NewCell());
        while (table.Columns.Count < width) table.Columns.Add(NewColumn());

        TableCell? last = null;
        for (var r = 0; r < sourceRows.Count; r++)
        {
            for (var c = 0; c < sourceRows[r].Cells.Count; c++)
            {
                var from = sourceRows[r].Cells[c];
                var to = Rows(table)[startRow + r].Cells[startColumn + c];
                var moved = from.Blocks.ToList();
                from.Blocks.Clear();
                to.Blocks.Clear();
                to.Blocks.AddRange(moved);
                if (to.Blocks.Count == 0) to.Blocks.Add(new Paragraph());
                last = to;
            }
        }

        if (last is not null) box.CaretPosition = last.ContentEnd.GetInsertionPosition(LogicalDirection.Backward);
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

        var last = (TableRow)cell.Parent;
        var added = NewRow(last.Cells.Count);
        ((TableRowGroup)last.Parent).Rows.Add(added);
        Focus(box, added.Cells[0]);
    }
}

public enum PasteIntoCell { NotHandled, Overwritten, AsText }
