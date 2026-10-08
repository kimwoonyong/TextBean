using System.Windows.Documents;

namespace TextBean.Views.Platform;

/// <summary>
/// 칸 지도 (D-174). 표를 「행 × 열 격자」로 펼친다 — 각 칸의 실제 자리(행 · 열)와 크기(세로 · 가로 칸 수), 각 격자 자리를 덮은 칸.
/// 칸을 합치면 「칸 번호 = 열 번호」가 깨진다 — 가로로 합친 행은 칸 수가 줄고, 세로로 합친 칸 아래 행도 칸 수가 준다 [실측 — P3].
/// 행 · 열 넣기 · 지우기 · 선택 · 합치기는 모두 이 지도 위에서 계산한다. 지도는 그때그때 새로 만든다(표가 작다).
/// </summary>
public sealed class TableGrid
{
    private readonly TableCell?[,] _slots;
    private readonly Dictionary<TableCell, (int Row, int Column)> _origins = [];

    public Table Table { get; }

    public IReadOnlyList<TableRow> Rows { get; }

    public int RowCount => Rows.Count;

    public int ColumnCount { get; }

    private TableGrid(Table table, IReadOnlyList<TableRow> rows, int columns)
    {
        Table = table;
        Rows = rows;
        ColumnCount = columns;
        _slots = new TableCell?[rows.Count, columns];
    }

    public static TableGrid Build(Table table)
    {
        var rows = table.RowGroups.SelectMany(g => g.Rows).ToList();

        // 열 수 = 선언된 열과 실제로 칸이 차지하는 폭 중 큰 쪽 — 밖에서 온 표가 어긋나도 칸을 잃지 않게
        var width = table.Columns.Count;
        var spill = new int[rows.Count + 1];
        for (var r = 0; r < rows.Count; r++)
        {
            var used = spill[r] + rows[r].Cells.Sum(c => Math.Max(1, c.ColumnSpan));
            width = Math.Max(width, used);
            foreach (var cell in rows[r].Cells)
                for (var below = r + 1; below < Math.Min(rows.Count, r + Math.Max(1, cell.RowSpan)); below++)
                    spill[below] += Math.Max(1, cell.ColumnSpan);
        }

        var grid = new TableGrid(table, rows, width);
        for (var r = 0; r < rows.Count; r++)
        {
            var c = 0;
            foreach (var cell in rows[r].Cells)
            {
                while (c < width && grid._slots[r, c] is not null) c++;
                if (c >= width) break;

                grid._origins[cell] = (r, c);
                for (var dr = 0; dr < Math.Max(1, cell.RowSpan) && r + dr < rows.Count; dr++)
                    for (var dc = 0; dc < Math.Max(1, cell.ColumnSpan) && c + dc < width; dc++)
                        grid._slots[r + dr, c + dc] = cell;
                c += Math.Max(1, cell.ColumnSpan);
            }
        }
        return grid;
    }

    public TableCell? At(int row, int column)
        => row >= 0 && row < RowCount && column >= 0 && column < ColumnCount ? _slots[row, column] : null;

    public (int Row, int Column) Origin(TableCell cell) => _origins[cell];

    public bool Contains(TableCell cell) => _origins.ContainsKey(cell);

    /// 칸이 덮는 마지막 행 · 열(포함).
    public (int Row, int Column) Corner(TableCell cell)
    {
        var (row, column) = _origins[cell];
        return (Math.Min(RowCount - 1, row + Math.Max(1, cell.RowSpan) - 1), Math.Min(ColumnCount - 1, column + Math.Max(1, cell.ColumnSpan) - 1));
    }

    public bool HasMerged => _origins.Keys.Any(c => c.RowSpan > 1 || c.ColumnSpan > 1);

    /// 읽는 순서(행 → 열)의 칸들.
    public IEnumerable<TableCell> Cells => _origins.OrderBy(kv => kv.Value.Row).ThenBy(kv => kv.Value.Column).Select(kv => kv.Key);

    /// <summary>
    /// 두 칸을 모서리로 하는 직사각형. 걸친 합친 칸이 있으면 그 칸 전체가 들어가도록 넓힌다(더 넓힐 것이 없을 때까지).
    /// </summary>
    public Area Span(TableCell a, TableCell b)
    {
        var (ar, ac) = Origin(a);
        var (br, bc) = Origin(b);
        var (aer, aec) = Corner(a);
        var (ber, bec) = Corner(b);
        var area = new Area(Math.Min(ar, br), Math.Min(ac, bc), Math.Max(aer, ber), Math.Max(aec, bec));

        for (var grown = true; grown;)
        {
            grown = false;
            foreach (var cell in CellsIn(area).ToList())
            {
                var (r, c) = Origin(cell);
                var (er, ec) = Corner(cell);
                var wider = new Area(Math.Min(area.Top, r), Math.Min(area.Left, c), Math.Max(area.Bottom, er), Math.Max(area.Right, ec));
                if (wider == area) continue;
                area = wider;
                grown = true;
            }
        }
        return area;
    }

    /// 영역과 겹치는 칸들(읽는 순서).
    public IEnumerable<TableCell> CellsIn(Area area)
    {
        var seen = new HashSet<TableCell>();
        for (var r = area.Top; r <= area.Bottom; r++)
            for (var c = area.Left; c <= area.Right; c++)
                if (At(r, c) is { } cell && seen.Add(cell)) yield return cell;
    }

    /// 행 row 의 칸 목록에서 격자 열 column 자리에 새 칸을 넣을 번호 — 그 행에서 시작하고 column 보다 왼쪽에 있는 칸 수.
    public int InsertIndex(int row, int column)
        => Rows[row].Cells.Count(c => _origins.TryGetValue(c, out var o) && o.Column < column);

    /// 격자의 직사각형 영역(포함 좌표).
    public readonly record struct Area(int Top, int Left, int Bottom, int Right);
}
