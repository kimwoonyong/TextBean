using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TextBean.Models;
using TextBean.ViewModels;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 서식 본문의 표 (D-131 ~ D-136). 화면 밖 · 포커스 없음 — 명령은 본문을 대상으로 직접 부른다.
/// </summary>
[Collection(WpfScreenCollection.Name)]
public class RichTableTests
{
    private static RichTextBox Body(params Block[] blocks)
    {
        var box = new RichTextBox();
        RichBodyBehavior.SetAttach(box, true);
        var document = new FlowDocument();
        document.Blocks.AddRange(blocks);
        box.Document = document;
        return box;
    }

    private static Table TableIn(RichTextBox box) => box.Document.Blocks.OfType<Table>().Single();

    private static List<TableRow> Rows(Table table) => [.. table.RowGroups.SelectMany(g => g.Rows)];

    private static TableCell Cell(Table table, int row, int column) => Rows(table)[row].Cells[column];

    private static string TextOf(TableCell cell) => new TextRange(cell.ContentStart, cell.ContentEnd).Text.TrimEnd('\r', '\n');

    private static Table Filled(int rows, int columns)
    {
        var table = RichTable.Create(rows, columns);
        for (var r = 0; r < rows; r++)
            for (var c = 0; c < columns; c++)
                ((Paragraph)Cell(table, r, c).Blocks.FirstBlock).Inlines.Add(new Run($"r{r}c{c}"));
        return table;
    }

    private static void CaretIn(RichTextBox box, TableCell cell)
        => box.CaretPosition = cell.ContentStart.GetInsertionPosition(LogicalDirection.Forward);

    // ── 검색용 글자 (D-135) ──────────────────────────────────────────────────

    [Fact]
    public void 표는_칸_사이_탭_행_사이_줄바꿈으로_뽑히고_위치가_맞다() => Run(() =>
    {
        var table = Filled(2, 3);
        Cell(table, 1, 1).Blocks.Add(new Paragraph(new Run("둘째 문단")));
        var box = Body(new Paragraph(new Run("앞")), table, new Paragraph(new Run("뒤")));

        var map = RichTextMap.Build(box.Document);

        Assert.Equal("앞\r\nr0c0\tr0c1\tr0c2\r\nr1c0\tr1c1\r\n둘째 문단\tr1c2\r\n뒤", map.Text);
        string Between(string piece)
        {
            var at = map.Text.IndexOf(piece, StringComparison.Ordinal);
            return new TextRange(map.PointerAt(at)!, map.PointerAt(at + piece.Length)!).Text;
        }
        Assert.Equal("r0c1", Between("r0c1"));
        Assert.Equal("둘째", Between("둘째"));
        Assert.Contains("r1c0", Between("r0c2\r\nr1c0"));                    // 행에 걸친 일치
    });

    [Fact]
    public void 표로_시작하는_문서는_앞에_줄바꿈이_없다() => Run(() =>
    {
        var box = Body(Filled(1, 2), new Paragraph(new Run("뒤")));

        Assert.Equal("r0c0\tr0c1\r\n뒤", RichTextMap.Build(box.Document).Text);
    });

    // ── 넣기 (D-136) ─────────────────────────────────────────────────────────

    [Fact]
    public void 빈_문단에서_넣으면_그_앞에_들어가고_캐럿은_첫_칸이다() => Run(() =>
    {
        var box = Body(new Paragraph(new Run("제목")), new Paragraph());
        box.CaretPosition = box.Document.Blocks.LastBlock.ContentStart;

        RichTable.InsertTable.Execute("2x3", box);

        var blocks = box.Document.Blocks.ToList();
        Assert.Equal(3, blocks.Count);
        var table = Assert.IsType<Table>(blocks[1]);
        Assert.Equal((2, 3, 3), (Rows(table).Count, Rows(table)[0].Cells.Count, table.Columns.Count));
        Assert.IsType<Paragraph>(blocks[2]);
        Assert.Same(Cell(table, 0, 0), RichTable.CellAt(box));
    });

    [Fact]
    public void 글자_문단에서_넣으면_뒤에_들어가고_문서_끝이면_빈_문단이_붙는다() => Run(() =>
    {
        var box = Body(new Paragraph(new Run("abc")));
        box.CaretPosition = box.Document.ContentEnd;

        RichTable.InsertTable.Execute("1x1", box);

        var blocks = box.Document.Blocks.ToList();
        Assert.Equal(3, blocks.Count);
        Assert.Equal("abc", new TextRange(blocks[0].ContentStart, blocks[0].ContentEnd).Text);
        Assert.IsType<Table>(blocks[1]);
        Assert.True(new TextRange(blocks[2].ContentStart, blocks[2].ContentEnd).IsEmpty);
    });

    [Fact]
    public void 표_안에서_넣으면_그_표_뒤에_들어간다() => Run(() =>
    {
        var first = Filled(1, 1);
        var box = Body(first, new Paragraph(new Run("뒤")));
        CaretIn(box, Cell(first, 0, 0));

        RichTable.InsertTable.Execute("1x2", box);

        var blocks = box.Document.Blocks.ToList();
        Assert.Same(first, blocks[0]);
        Assert.Equal(2, Rows((Table)blocks[1])[0].Cells.Count);
        Assert.Single(Rows(first)[0].Cells);                                  // 바깥 표 안에 넣지 않았다
    });

    [Fact]
    public void 넣기는_실행취소_한_번에_사라지고_크기가_틀리면_안_된다() => Run(() =>
    {
        var box = Body(new Paragraph(new Run("abc")));

        Assert.False(RichTable.InsertTable.CanExecute("0x3", box));
        Assert.False(RichTable.InsertTable.CanExecute("9x1", box));
        Assert.False(RichTable.InsertTable.CanExecute("3", box));
        var window = new Window
        {
            Left = -20000, Top = -20000, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = box
        };
        window.Show();
        try
        {
            RichTable.InsertTable.Execute("3x3", box);
            Assert.Single(box.Document.Blocks.OfType<Table>());
            Assert.True(box.CanUndo);

            box.Undo();

            Assert.Empty(box.Document.Blocks.OfType<Table>());
            Assert.Equal("abc", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.TrimEnd());
        }
        finally
        {
            window.Close();
        }
    });

    // ── 행 · 열 · 지우기 (D-132) ─────────────────────────────────────────────

    [Fact]
    public void 행과_열을_넣고_지운다() => Run(() =>
    {
        var box = Body(Filled(2, 2), new Paragraph());
        var table = TableIn(box);
        CaretIn(box, Cell(table, 0, 1));

        RichTable.InsertRowBelow.Execute(null, box);
        Assert.Equal(["r0c0", "", "r1c0"], Rows(table).Select(r => TextOf(r.Cells[0])));
        Assert.Same(Cell(table, 1, 1), RichTable.CellAt(box));               // 새 행 같은 열

        RichTable.InsertRowAbove.Execute(null, box);
        Assert.Equal(4, Rows(table).Count);

        CaretIn(box, Cell(table, 0, 0));
        RichTable.InsertColumnLeft.Execute(null, box);
        RichTable.InsertColumnRight.Execute(null, box);
        Assert.All(Rows(table), r => Assert.Equal(4, r.Cells.Count));
        Assert.Equal(4, table.Columns.Count);
        Assert.Equal(["", "", "r0c0", "r0c1"], Rows(table)[0].Cells.Select(TextOf));

        CaretIn(box, Cell(table, 0, 2));
        RichTable.DeleteColumn.Execute(null, box);
        Assert.Equal(["", "", "r0c1"], Rows(table)[0].Cells.Select(TextOf));
        Assert.Equal(3, table.Columns.Count);

        CaretIn(box, Cell(table, 0, 0));
        RichTable.DeleteRow.Execute(null, box);
        Assert.Equal(3, Rows(table).Count);
        Assert.Equal(["", "", "r1c1"], Rows(table)[2].Cells.Select(TextOf));   // 첫 행이 빠지고 원래 둘째 행이 끝에 남는다
    });

    /// 빈 표를 남기지 않는다 — 남으면 지울 길이 없는 틀만 남는다
    [Fact]
    public void 마지막_행이나_열을_지우면_표가_없어진다() => Run(() =>
    {
        var byRow = Body(Filled(1, 2), new Paragraph(new Run("뒤")));
        CaretIn(byRow, Cell(TableIn(byRow), 0, 0));
        RichTable.DeleteRow.Execute(null, byRow);
        Assert.Empty(byRow.Document.Blocks.OfType<Table>());

        var byColumn = Body(Filled(2, 1), new Paragraph(new Run("뒤")));
        CaretIn(byColumn, Cell(TableIn(byColumn), 1, 0));
        RichTable.DeleteColumn.Execute(null, byColumn);
        Assert.Empty(byColumn.Document.Blocks.OfType<Table>());
    });

    private static void SelectCells(RichTextBox box, TableCell from, TableCell to)
        => box.Selection.Select(from.ContentStart.GetInsertionPosition(LogicalDirection.Forward),
                                to.ContentEnd.GetInsertionPosition(LogicalDirection.Backward));

    [Fact]
    public void 여러_칸을_고르면_걸친_행이나_열을_모두_지운다() => Run(() =>
    {
        var byRow = Body(Filled(4, 3), new Paragraph());
        var rows = TableIn(byRow);
        SelectCells(byRow, Cell(rows, 1, 1), Cell(rows, 2, 2));
        RichTable.DeleteRow.Execute(null, byRow);
        Assert.Equal(["r0c0", "r3c0"], Rows(rows).Select(r => TextOf(r.Cells[0])));

        var byColumn = Body(Filled(4, 3), new Paragraph());
        var columns = TableIn(byColumn);
        SelectCells(byColumn, Cell(columns, 2, 2), Cell(columns, 1, 1));          // 거꾸로 골라도 같다
        RichTable.DeleteColumn.Execute(null, byColumn);
        Assert.All(Rows(columns), r => Assert.Single(r.Cells));
        Assert.Single(columns.Columns);
        Assert.Equal(["r0c0", "r1c0", "r2c0", "r3c0"], Rows(columns).Select(r => TextOf(r.Cells[0])));
    });

    [Fact]
    public void 모든_행이나_열에_걸치면_표를_지운다() => Run(() =>
    {
        var byRow = Body(Filled(3, 2), new Paragraph(new Run("뒤")));
        SelectCells(byRow, Cell(TableIn(byRow), 0, 1), Cell(TableIn(byRow), 2, 1));
        RichTable.DeleteRow.Execute(null, byRow);
        Assert.Empty(byRow.Document.Blocks.OfType<Table>());

        var byColumn = Body(Filled(2, 3), new Paragraph(new Run("뒤")));
        SelectCells(byColumn, Cell(TableIn(byColumn), 1, 0), Cell(TableIn(byColumn), 1, 2));
        RichTable.DeleteColumn.Execute(null, byColumn);
        Assert.Empty(byColumn.Document.Blocks.OfType<Table>());
    });

    [Fact]
    public void 선택_끝이_표_밖이면_시작_칸의_행_하나만_지운다() => Run(() =>
    {
        var box = Body(Filled(3, 2), new Paragraph(new Run("뒤")));
        var table = TableIn(box);
        box.Selection.Select(Cell(table, 1, 0).ContentStart.GetInsertionPosition(LogicalDirection.Forward), box.Document.ContentEnd);

        RichTable.DeleteRow.Execute(null, box);

        Assert.Equal(["r0c0", "r2c0"], Rows(table).Select(r => TextOf(r.Cells[0])));
    });

    [Fact]
    public void 여러_행_지우기는_실행취소_한_번에_되돌아간다() => Run(() =>
    {
        var box = Body(Filled(4, 2), new Paragraph());
        var window = new Window
        {
            Left = -20000, Top = -20000, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = box
        };
        window.Show();
        try
        {
            SelectCells(box, Cell(TableIn(box), 0, 0), Cell(TableIn(box), 2, 1));
            RichTable.DeleteRow.Execute(null, box);
            Assert.Single(Rows(TableIn(box)));

            box.Undo();

            Assert.Equal(["r0c0", "r1c0", "r2c0", "r3c0"], Rows(TableIn(box)).Select(r => TextOf(r.Cells[0])));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void 표_지우기는_표를_없애고_캐럿을_다음_문단에_둔다() => Run(() =>
    {
        var box = Body(new Paragraph(new Run("앞")), Filled(2, 2), new Paragraph(new Run("뒤")));
        CaretIn(box, Cell(TableIn(box), 1, 1));

        RichTable.DeleteTable.Execute(null, box);

        Assert.Empty(box.Document.Blocks.OfType<Table>());
        Assert.Equal("뒤", new TextRange(box.CaretPosition.Paragraph!.ContentStart, box.CaretPosition.Paragraph.ContentEnd).Text);

        var only = Body(Filled(1, 1));                                        // 표만 있던 문서
        CaretIn(only, Cell(TableIn(only), 0, 0));
        RichTable.DeleteTable.Execute(null, only);
        Assert.IsType<Paragraph>(Assert.Single(only.Document.Blocks));
    });

    // ── 칸 이동 (D-133) ──────────────────────────────────────────────────────

    [Fact]
    public void 다음_칸은_줄을_넘고_마지막_칸이면_행을_넣는다() => Run(() =>
    {
        var box = Body(Filled(2, 2), new Paragraph());
        var table = TableIn(box);
        CaretIn(box, Cell(table, 0, 1));

        RichTable.NextCell.Execute(null, box);
        Assert.Same(Cell(table, 1, 0), RichTable.CellAt(box));

        CaretIn(box, Cell(table, 1, 1));
        RichTable.NextCell.Execute(null, box);
        Assert.Equal(3, Rows(table).Count);
        Assert.Same(Cell(table, 2, 0), RichTable.CellAt(box));

        RichTable.PreviousCell.Execute(null, box);
        Assert.Same(Cell(table, 1, 1), RichTable.CellAt(box));

        CaretIn(box, Cell(table, 0, 0));
        RichTable.PreviousCell.Execute(null, box);
        Assert.Same(Cell(table, 0, 0), RichTable.CellAt(box));               // 첫 칸에서 이전은 그대로
    });

    [Fact]
    public void Tab_은_표_안에서만_가로챈다() => Run(() =>
    {
        var box = Body(new Paragraph(new Run("앞")), Filled(1, 2), new Paragraph());
        var window = new Window
        {
            Left = -20000, Top = -20000, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = box
        };
        window.Show();
        try
        {
            bool PressTab()
            {
                var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(box)!, 0, Key.Tab)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent
                };
                box.RaiseEvent(args);
                return args.Handled;
            }

            box.CaretPosition = box.Document.Blocks.FirstBlock.ContentEnd;
            Assert.False(PressTab());                                          // 표 밖 — WPF 기본(탭 글자)으로 간다

            var table = TableIn(box);
            CaretIn(box, Cell(table, 0, 0));
            Assert.True(PressTab());
            Assert.Same(Cell(table, 0, 1), RichTable.CellAt(box));
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void 표_밖이나_읽기_전용이면_표_명령이_안_되고_메뉴_항목이_숨는다() => Run(() =>
    {
        var box = Body(new Paragraph(new Run("앞")), Filled(1, 1), new Paragraph());
        var menu = new ContextMenu();
        var copy = new MenuItem { Header = "복사" };
        var row = new MenuItem { Header = "행 지우기", Tag = RichTable.MenuTag };
        var line = new Separator { Tag = RichTable.MenuTag };
        menu.Items.Add(copy); menu.Items.Add(line); menu.Items.Add(row);

        box.CaretPosition = box.Document.Blocks.FirstBlock.ContentEnd;
        RichTable.UpdateMenu(box, menu);
        Assert.False(RichTable.DeleteRow.CanExecute(null, box));
        Assert.Equal((Visibility.Visible, Visibility.Collapsed, Visibility.Collapsed), (copy.Visibility, line.Visibility, row.Visibility));

        CaretIn(box, Cell(TableIn(box), 0, 0));
        RichTable.UpdateMenu(box, menu);
        Assert.True(RichTable.DeleteRow.CanExecute(null, box));
        Assert.Equal((Visibility.Visible, Visibility.Visible), (line.Visibility, row.Visibility));

        box.IsReadOnly = true;
        RichTable.UpdateMenu(box, menu);
        Assert.False(RichTable.DeleteTable.CanExecute(null, box));
        Assert.False(RichTable.InsertTable.CanExecute("1x1", box));
        Assert.Equal(Visibility.Collapsed, row.Visibility);
    });

    // ── 칸 안 붙여넣기 (D-137) ───────────────────────────────────────────────

    /// 앱 안에서 복사한 것처럼 — 실제 복사 경로가 만드는 DataObject 를 그대로 쓴다(클립보드는 쓰지 않는다)
    private static DataObject Copied(TextPointer from, TextPointer to)
        => ClipboardService.BuildDataObject(RichBodyBehavior.PayloadOf(new TextRange(from, to)));

    private static DataObject CopiedTable(Table table)
        => Copied(Cell(table, 0, 0).ContentStart, Rows(table)[^1].Cells[^1].ContentEnd);

    private static DataObjectPastingEventArgs Paste(RichTextBox box, DataObject data)
    {
        var args = new DataObjectPastingEventArgs(data, isDragDrop: false, DataFormats.XamlPackage) { RoutedEvent = DataObject.PastingEvent };
        box.RaiseEvent(args);
        return args;
    }

    private static int TablesIn(FlowDocument document)
    {
        static int Count(DependencyObject at)
            => (at is Table ? 1 : 0) + LogicalTreeHelper.GetChildren(at).OfType<DependencyObject>().Sum(Count);
        return Count(document);
    }

    private static Table Letters(int rows, int columns, char first = 'A')
    {
        var table = RichTable.Create(rows, columns);
        for (var i = 0; i < rows * columns; i++)
            ((Paragraph)Cell(table, i / columns, i % columns).Blocks.FirstBlock).Inlines.Add(new Run(((char)(first + i)).ToString()));
        return table;
    }

    [Fact]
    public void 칸에_표를_붙이면_그_칸부터_덮어쓴다() => Run(() =>
    {
        var source = Body(Letters(2, 2));
        var box = Body(Filled(2, 3), new Paragraph());
        var table = TableIn(box);
        CaretIn(box, Cell(table, 0, 1));

        var args = Paste(box, CopiedTable(TableIn(source)));

        Assert.True(args.CommandCancelled);                                   // 기본 붙여넣기(표 안에 표)를 막았다
        Assert.Equal(1, TablesIn(box.Document));
        Assert.Equal(["r0c0", "A", "B"], Rows(table)[0].Cells.Select(TextOf));
        Assert.Equal(["r1c0", "C", "D"], Rows(table)[1].Cells.Select(TextOf));
        Assert.Same(Cell(table, 1, 2), RichTable.CellAt(box));                // 마지막으로 채운 칸
    });

    [Fact]
    public void 칸이_모자라면_행과_열을_늘려_덮어쓴다() => Run(() =>
    {
        var source = Body(Letters(2, 2));
        var box = Body(Filled(2, 2), new Paragraph());
        var table = TableIn(box);
        CaretIn(box, Cell(table, 1, 1));

        Paste(box, CopiedTable(TableIn(source)));

        Assert.Equal(3, Rows(table).Count);
        Assert.All(Rows(table), r => Assert.Equal(3, r.Cells.Count));
        Assert.Equal(3, table.Columns.Count);
        Assert.Equal(["r0c0", "r0c1", ""], Rows(table)[0].Cells.Select(TextOf));
        Assert.Equal(["r1c0", "A", "B"], Rows(table)[1].Cells.Select(TextOf));
        Assert.Equal(["", "C", "D"], Rows(table)[2].Cells.Select(TextOf));
    });

    [Fact]
    public void 글자와_표가_섞이면_칸에_글자로_넣는다() => Run(() =>
    {
        var source = Body(new Paragraph(new Run("서버 목록")), Letters(1, 2));
        var box = Body(Filled(1, 2), new Paragraph());
        CaretIn(box, Cell(TableIn(box), 0, 1));

        var args = Paste(box, Copied(source.Document.ContentStart, source.Document.ContentEnd));

        Assert.False(args.CommandCancelled);
        Assert.Equal(DataFormats.UnicodeText, args.FormatToApply);
        Assert.False(args.DataObject.GetDataPresent(DataFormats.XamlPackage));
        Assert.Contains("A\tB", (string)args.DataObject.GetData(DataFormats.UnicodeText));
    });

    [Fact]
    public void 표_없는_서식이나_표_밖이면_지금처럼_서식째_붙인다() => Run(() =>
    {
        var source = Body(Letters(1, 2));
        var oneCell = Cell(TableIn(source), 0, 0);
        var box = Body(new Paragraph(new Run("앞")), Filled(1, 1), new Paragraph());

        CaretIn(box, Cell(TableIn(box), 0, 0));
        var inCell = Paste(box, Copied(oneCell.ContentStart.GetInsertionPosition(LogicalDirection.Forward),
                                       oneCell.ContentEnd.GetInsertionPosition(LogicalDirection.Backward)));
        Assert.False(inCell.CommandCancelled);
        Assert.Equal(DataFormats.XamlPackage, inCell.FormatToApply);
        Assert.True(inCell.DataObject.GetData(DataFormats.XamlPackage) is System.IO.Stream { Position: 0 });   // 표가 있나 읽어 본 뒤 되돌렸다 — 기본 붙여넣기가 처음부터 읽는다

        box.CaretPosition = box.Document.Blocks.FirstBlock.ContentEnd;           // 표 밖
        var outside = Paste(box, CopiedTable(TableIn(source)));
        Assert.False(outside.CommandCancelled);
        Assert.Equal(DataFormats.XamlPackage, outside.FormatToApply);
        Assert.True(outside.DataObject.GetData(DataFormats.XamlPackage) is System.IO.Stream { Position: 0 });   // 기본 붙여넣기가 처음부터 읽는다
    });

    [Fact]
    public void 덮어쓰기는_실행취소_한_번에_되돌아간다() => Run(() =>
    {
        var source = Body(Letters(2, 2));
        var box = Body(Filled(1, 1), new Paragraph());
        var window = new Window
        {
            Left = -20000, Top = -20000, Width = 300, Height = 200, ShowActivated = false, ShowInTaskbar = false,
            WindowStyle = WindowStyle.None, Content = box
        };
        window.Show();
        try
        {
            CaretIn(box, Cell(TableIn(box), 0, 0));
            Paste(box, CopiedTable(TableIn(source)));
            Assert.Equal(2, Rows(TableIn(box)).Count);

            box.Undo();

            var table = TableIn(box);
            Assert.Single(Rows(table));
            Assert.Equal(["r0c0"], Rows(table)[0].Cells.Select(TextOf));
        }
        finally
        {
            window.Close();
        }
    });

    // ── 격자 (D-131) ─────────────────────────────────────────────────────────

    [Fact]
    public void 격자는_좌표를_크기로_바꾸고_고르면_그_크기로_넣는다() => Run(() =>
    {
        Assert.Equal((1, 1), TableSizePicker.SizeAt(new Point(0, 0)));
        Assert.Equal((4, 3), TableSizePicker.SizeAt(new Point(18 * 2 + 1, 18 * 3 + 1)));      // y = 행, x = 열
        Assert.Equal((8, 8), TableSizePicker.SizeAt(new Point(999, 999)));

        var box = Body(new Paragraph(new Run("abc")));
        var picker = new TableSizePicker { Command = RichTable.InsertTable, CommandTarget = box };
        var picked = 0;
        picker.Picked += (_, _) => picked++;

        picker.Highlight((3, 4));
        Assert.Equal((3, 4), picker.Current);
        picker.Pick((3, 4));

        var table = TableIn(box);
        Assert.Equal((3, 4), (Rows(table).Count, Rows(table)[0].Cells.Count));
        Assert.Equal(1, picked);
    });

    // ── 저장 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void 표가_저장되고_다시_열면_테두리와_칸_글자가_남는다() => Run(() =>
    {
        using var vault = new TempVault();
        var store = TestKeys.Store(vault.Root);
        var path = Path.Combine(vault.Root, "a.tbx");
        Wait(store.CreateAsync(path));
        var vm = new EditorViewModel(store, new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer());
        Wait(vm.LoadAsync(path));
        var box = new RichTextBox { DataContext = vm };
        RichBodyBehavior.SetAttach(box, true);

        RichTable.InsertTable.Execute("1x2", box);
        box.CaretPosition.InsertTextInRun("계정");
        RichTable.NextCell.Execute(null, box);
        box.CaretPosition.InsertTextInRun("비밀값");
        Assert.True(vm.IsDirty);
        Assert.True(Wait(vm.TrySaveAsync()));

        var again = new EditorViewModel(store, new FakeClipboard(), new FakeDialogs(), new FakeAutoSaveTimer());
        Wait(again.LoadAsync(path));
        var reopened = new RichTextBox { DataContext = again };
        RichBodyBehavior.SetAttach(reopened, true);

        var table = TableIn(reopened);
        Assert.Equal(["계정", "비밀값"], Rows(table)[0].Cells.Select(TextOf));
        Assert.Equal(new Thickness(0, 0, 1, 1), Cell(table, 0, 1).BorderThickness);
        Assert.Equal(Color.FromRgb(0xD3, 0xD1, 0xC7), ((SolidColorBrush)Cell(table, 0, 1).BorderBrush).Color);
        Assert.Contains("계정\t비밀값", again.Text);
    });
}
