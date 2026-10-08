using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using TextBean.Models;
using TextBean.ViewModels;

namespace TextBean.Views.Platform;

/// <summary>
/// 서식 본문(RichTextBox)과 편집기를 잇는다 (D-125). FlowDocument 는 바인딩할 수 없어 첨부 동작이 맡는다.
/// <list type="bullet">
/// <item>편집기가 문서를 불러오면(Rich 알림) 서식 문서를 새로 만든다 — 실행취소 기록도 함께 비워진다(탭마다 문서 하나).</item>
/// <item>본문이 바뀌면 곧바로 MarkEdited, 검색용 글자는 한 번에 모아 뒤에 SyncText 로 보낸다 — 100만 자에서 한 번 뽑는 데 약 45ms [실측].</item>
/// <item>저장 · 전체 복사 때 편집기가 부르는 CaptureBody · CaptureAllForCopy 를 등록한다.</item>
/// <item>붙여넣기를 거른다(PasteFilter) — 밖에서 온 것은 글자만 (D-126).</item>
/// <item>서식 6종 밖의 RichTextBox 기본 키를 막는다 (D-127).</item>
/// </list>
/// 복사 가로채기는 TextBox 와 같이 EditorBehavior.InterceptCopy 가 맡는다 (D-007).
/// </summary>
public static class RichBodyBehavior
{
    /// 서식 밖의 RichTextBox 기본 키 [실측 — 클래스 입력 바인딩 중 TextBox 에 없는 것]. Ctrl+B · I 는 그대로, Ctrl+U · Ctrl+[ · Ctrl+] 는 RichFormat 이 갈아 끼운다.
    public static readonly IReadOnlyList<KeyGesture> BlockedGestures =
    [
        new(Key.E, ModifierKeys.Control), new(Key.L, ModifierKeys.Control),
        new(Key.R, ModifierKeys.Control), new(Key.J, ModifierKeys.Control),                 // 정렬
        new(Key.D1, ModifierKeys.Control), new(Key.D2, ModifierKeys.Control),
        new(Key.D5, ModifierKeys.Control),                                                   // 줄 간격
        // 글자 크기(Ctrl+[ · Ctrl+]) 는 막지 않는다 — RichFormat 이 목록의 다음 · 이전 크기로 갈아 끼운다 (D-152)
        new(Key.T, ModifierKeys.Control), new(Key.T, ModifierKeys.Control | ModifierKeys.Shift), // 들여쓰기
        new(Key.L, ModifierKeys.Control | ModifierKeys.Shift), new(Key.N, ModifierKeys.Control | ModifierKeys.Shift),
        new(Key.R, ModifierKeys.Control | ModifierKeys.Shift),                               // 목록
        new(Key.OemPlus, ModifierKeys.Control), new(Key.OemPlus, ModifierKeys.Control | ModifierKeys.Shift), // 첨자
        new(Key.Space, ModifierKeys.Control),                                                // 서식 초기화
        new(Key.C, ModifierKeys.Control | ModifierKeys.Shift),                               // 서식 복사
    ];

    public static readonly DependencyProperty AttachProperty =
        DependencyProperty.RegisterAttached("Attach", typeof(bool), typeof(RichBodyBehavior),
            new PropertyMetadata(false, OnAttachChanged));

    public static void SetAttach(DependencyObject element, bool value) => element.SetValue(AttachProperty, value);

    public static bool GetAttach(DependencyObject element) => (bool)element.GetValue(AttachProperty);

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached("State", typeof(Hookup), typeof(RichBodyBehavior));

    /// 마지막으로 뽑은 글자 ↔ 위치 대응. 찾기 강조가 쓴다. 본문이 바뀐 직후에는 낡았을 수 있다 — 쓰는 쪽이 글자 길이로 거른다.
    public static RichTextMap? GetMap(RichTextBox box) => (box.GetValue(StateProperty) as Hookup)?.Map;

    private static void OnAttachChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RichTextBox box || !(bool)e.NewValue || box.GetValue(StateProperty) is not null) return;

        box.SetValue(StateProperty, new Hookup(box));
    }

    /// 그림 하나만 고른 범위면 그 그림 PNG 도 싣는다 — 밖의 프로그램용 (D-143).
    public static ClipboardPayload PayloadOf(TextRange range)
        => new(range.Text, RichTextMap.SaveRtf(range), RichTextMap.Save(range), RichImage.PngOf(range));

    /// 이 본문이 메모리 패키지에 올린 그림 원본들 (D-146). 붙지 않은 본문(시험)이면 버리는 목록.
    public static ICollection<Uri> OwnedImages(RichTextBox box) => (box.GetValue(StateProperty) as Hookup)?.Images ?? [];

    /// 지금 고른 그림의 손잡이. 없으면 null.
    public static ImageResizeAdorner? GetResizer(RichTextBox box) => (box.GetValue(StateProperty) as Hookup)?.Resizer;

    private sealed class Hookup
    {
        private readonly RichTextBox _box;
        private EditorViewModel? _vm;
        private bool _loading;
        private bool _syncPending;

        public RichTextMap? Map { get; private set; }

        public List<Uri> Images { get; } = [];

        public ImageResizeAdorner? Resizer { get; private set; }

        public Hookup(RichTextBox box)
        {
            _box = box;
            RichFormat.Attach(box);
            RichTable.Attach(box);
            RichImage.Attach(box);
            foreach (var gesture in BlockedGestures)
                box.InputBindings.Add(new KeyBinding(ApplicationCommands.NotACommand, gesture));

            DataObject.AddPastingHandler(box, OnPasting);
            box.TextChanged += OnTextChanged;
            box.SelectionChanged += (_, _) => UpdateResizer();
            // 도구 모음 「13 ▾」 — 커서를 옮기거나 글이 바뀌면(실행취소 포함) 지금 크기를 다시 읽는다 (D-151)
            box.SelectionChanged += (_, _) => RichFormat.UpdateCurrentSize(box);
            box.TextChanged += (_, _) => RichFormat.UpdateCurrentSize(box);
            // 붙는 순간에는 본문 자원(FlowDocument 스타일 — 본문 글꼴 · 13)이 아직 없어 Fluent 기본(16)을 읽는다 [실측 — 렌더] — 다 만들어진 뒤 다시 읽는다 (D-163)
            box.Loaded += (_, _) => RichFormat.UpdateCurrentSize(box);
            box.IsVisibleChanged += (_, _) => UpdateResizer();      // 가려진 탭 위에 손잡이를 그리지 않는다
            box.DataContextChanged += (_, e) => Bind(e.NewValue as EditorViewModel);
            Bind(box.DataContext as EditorViewModel);
        }

        private void UpdateResizer()
        {
            if (Resizer is not null)
            {
                AdornerLayer.GetAdornerLayer(Resizer.AdornedElement)?.Remove(Resizer);
                Resizer = null;
            }

            if (_box.IsReadOnly || !_box.IsVisible || RichImage.SelectedImage(_box) is not { Child: Image image } container) return;

            // 막 넣거나 갈아 끼운 그림은 아직 그려지기 전이다 — 그려진 뒤 다시 본다(크기를 바꾼 직후 손잡이가 사라지지 않게) [실측 — 시험]
            if (!image.IsLoaded)
            {
                RoutedEventHandler? once = null;
                once = (_, _) => { image.Loaded -= once; UpdateResizer(); };
                image.Loaded += once;
                return;
            }

            if (AdornerLayer.GetAdornerLayer(image) is not { } layer) return;

            Resizer = new ImageResizeAdorner(image, container, _box);
            layer.Add(Resizer);
        }

        private void OnClosed(object? sender, EventArgs e) => RichImage.Release(Images);

        private void Bind(EditorViewModel? vm)
        {
            if (ReferenceEquals(_vm, vm)) return;

            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.Closed -= OnClosed;
                // 자기가 건 연결만 푼다 (계획 검토 R-2). 테마를 바꾸면 본문이 새로 만들어진다 [실측] — 옛 본문이 새 본문보다 늦게 풀리면
                // 새 본문의 연결까지 지워 저장이 옛 바이트를 쓰고, 바꾼 뒤 쓴 글이 저장에서 빠진다.
                if (Equals(_vm.CaptureBody, (Func<DocumentBody>)Capture)) _vm.CaptureBody = null;
                if (Equals(_vm.CaptureAllForCopy, (Func<ClipboardPayload>)CaptureAll)) _vm.CaptureAllForCopy = null;
            }

            _vm = vm;
            if (_vm is null) return;

            _vm.PropertyChanged += OnVmChanged;
            _vm.Closed += OnClosed;
            _vm.CaptureBody = Capture;
            _vm.CaptureAllForCopy = CaptureAll;
            Reload();
        }

        private ClipboardPayload CaptureAll() => PayloadOf(new TextRange(_box.Document.ContentStart, _box.Document.ContentEnd));

        private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditorViewModel.Rich)) Reload();
        }

        private void Reload()
        {
            if (_vm is null) return;

            // 새 탭은 경로가 정해지기 전에 이 템플릿으로 한 번 그려진다. .txt 로 정해지면 TextBox 로 바뀌므로 손을 뗀다 —
            // 남아 있으면 버려진 본문이 저장 · 복사 본문을 뽑고, 문서를 불러올 때마다 쓸데없이 서식 문서를 만든다.
            if (_vm.IsPlainTextFile)
            {
                Bind(null);
                return;
            }

            _loading = true;
            try
            {
                FlowDocument document;
                try
                {
                    document = _vm.Rich.Length > 0 ? RichTextMap.Load(_vm.Rich) : RichTextMap.FromPlain(_vm.Text);
                }
                catch (Exception ex)
                {
                    // 서식 바이트를 읽지 못하면 잠근다 — 빈 문서로 두면 자동 저장이 원본을 덮는다 (D-005)
                    _box.Document = new FlowDocument();
                    _vm.RichLoadFailed(ex);
                    Map = null;
                    return;
                }

                // 문서를 갈아 끼우면 실행취소 기록도 비워진다 — 전 문서에 올린 그림 원본은 더 쓰일 데가 없다 (D-146)
                _box.Document = document;
                RichImage.Release(Images);
                Map = RichTextMap.Build(document);
                _vm.SyncText(Map.Text);

                // 도구 모음 단추는 명령이 붙기 전 · 잠금이 정해지기 전에 한 번 묻고 만다 — 입력이 없으면 다시 묻지 않아 흐린 채 남는다 [실측 — 화면 밖 렌더]
                CommandManager.InvalidateRequerySuggested();
            }
            finally
            {
                _loading = false;
            }
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading || _vm is null) return;

            _vm.MarkEdited();
            if (_syncPending) return;

            // 키 입력마다 문서 전체를 걷지 않는다. 입력이 몰리면 한 번에 모아 뽑는다.
            _syncPending = true;
            _box.Dispatcher.BeginInvoke(DispatcherPriority.Background, SyncNow);
        }

        private void SyncNow()
        {
            _syncPending = false;
            if (_vm is null) return;

            Map = RichTextMap.Build(_box.Document);
            _vm.SyncText(Map.Text);
        }

        private DocumentBody Capture()
        {
            SyncNow();
            var all = new TextRange(_box.Document.ContentStart, _box.Document.ContentEnd);
            return new DocumentBody(Map!.Text, RichTextMap.Save(all));
        }

        /// <summary>
        /// 앱 안 서식을 직접 붙인다 (D-154). 고른 자리에 불러오고(기본 붙여넣기와 같은 TextRange.Load), 넣은 범위의 글꼴 이름만 지운다 —
        /// 크기 · 기울임 등 복사한 서식은 그대로다. 한 번의 변경이라 실행취소 한 번.
        /// 붙이기 전에 다시 담아 지우던 방식은 빈 문서 기본값(Georgia 16 · 양쪽 정렬)을 박았다 [실측 — 사용자 화면].
        /// 서식을 못 읽으면 false — 아무것도 넣지 않는다.
        /// </summary>
        /// 자리가 든 맨 바깥 블록(문서 바로 아래). 없으면 null.
        private static Block? TopBlock(TextPointer position)
        {
            Block? top = null;
            for (DependencyObject? at = position.Parent; at is TextElement element; at = element.Parent)
                if (element is Block block) top = block;
            return top;
        }

        private static bool PasteRich(RichTextBox box, IDataObject data)
        {
            if (box.IsReadOnly || data.GetData(DataFormats.XamlPackage) is not System.IO.Stream stream) return false;

            byte[] package;
            var position = stream.CanSeek ? stream.Position : 0;
            using (var copy = new System.IO.MemoryStream())
            {
                if (stream.CanSeek) stream.Position = 0;
                stream.CopyTo(copy);
                if (stream.CanSeek) stream.Position = position;
                package = copy.ToArray();
            }

            box.BeginChange();
            try
            {
                using (var source = new System.IO.MemoryStream(package, writable: false))
                    box.Selection.Load(source, DataFormats.XamlPackage);

                // 불러온 뒤 선택이 넣은 글자를 가리킨다 — 미리 잡아 둔 앞뒤 자리는 한 점으로 모여 쓸 수 없었다 [실측].
                // 선택 시작은 글자 안이라 그 글자 요소의 시작을 지나친다 — 걸친 문단 전체를 지운다(원래 글자엔 글꼴 이름이 없다).
                // 표를 붙였으면 문단 바깥(표 · 행 · 칸)도 걸친다 — 맨 바깥 블록까지 넓힌다.
                var selection = box.Selection;
                var start = TopBlock(selection.Start)?.ElementStart ?? selection.Start;
                var end = TopBlock(selection.End)?.ElementEnd ?? selection.End;
                RichTextMap.ClearFonts(start, end);
                DocumentColors.Prepare(start, end, AppTheme.IsDark);   // 복사본은 저장 색이다 — 같은 변경 안에서 지금 테마로 (D-162)
                box.CaretPosition = selection.End;
                return true;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                box.EndChange();
            }
        }

        private static void OnPasting(object sender, DataObjectPastingEventArgs e)
        {
            var choice = PasteFilter.Choose(e.DataObject);

            // 칸 안에 표가 든 서식 — 표 안에 표를 만들지 않는다 (D-137)
            if (choice == PasteChoice.Rich && sender is RichTextBox box)
            {
                switch (RichTable.Paste(box, e.DataObject))
                {
                    case PasteIntoCell.Overwritten:
                        e.CancelCommand();
                        return;
                    case PasteIntoCell.AsText:
                        choice = PasteChoice.PlainText;
                        break;
                }
            }

            switch (choice)
            {
                case PasteChoice.Rich:
                    // 기본 붙여넣기에 맡기면 복사한 상대 주소 글꼴(./#D2Coding)이 넣은 글자에 적힌다 — 직접 붙이고 글꼴 이름만 지운다 (D-154)
                    e.CancelCommand();
                    if (sender is RichTextBox richTarget) PasteRich(richTarget, e.DataObject);
                    break;

                case PasteChoice.Image:
                    // 기본 붙여넣기에 맡기지 않는다 — 메모리 그림이 들어가면 실행취소에서 사라진다 (D-146)
                    e.CancelCommand();
                    if (sender is RichTextBox { IsReadOnly: false } target
                        && e.DataObject.GetData(DataFormats.Bitmap, autoConvert: true) is System.Windows.Media.Imaging.BitmapSource raw)
                        RichImage.Insert(target, RichImage.Prepare(raw, OwnedImages(target)));
                    break;

                case PasteChoice.PlainText:
                    // 글자만 든 DataObject 로 갈아 끼운다 — 원래 것을 두면 RichTextBox 가 Rtf · Xaml 을 골라 받는다
                    var plain = new DataObject();
                    plain.SetText((string)e.DataObject.GetData(DataFormats.UnicodeText, autoConvert: true));
                    e.DataObject = plain;
                    e.FormatToApply = DataFormats.UnicodeText;
                    break;

                default:
                    e.CancelCommand();
                    break;
            }
        }
    }
}
