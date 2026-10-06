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
    /// 서식 6종 밖의 RichTextBox 기본 키 [실측 — 클래스 입력 바인딩 중 TextBox 에 없는 것]. Ctrl+B · I 는 그대로, Ctrl+U 는 RichFormat 이 갈아 끼운다.
    public static readonly IReadOnlyList<KeyGesture> BlockedGestures =
    [
        new(Key.E, ModifierKeys.Control), new(Key.L, ModifierKeys.Control),
        new(Key.R, ModifierKeys.Control), new(Key.J, ModifierKeys.Control),                 // 정렬
        new(Key.D1, ModifierKeys.Control), new(Key.D2, ModifierKeys.Control),
        new(Key.D5, ModifierKeys.Control),                                                   // 줄 간격
        new(Key.OemOpenBrackets, ModifierKeys.Control), new(Key.Oem6, ModifierKeys.Control),  // 글자 크기
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

    public static ClipboardPayload PayloadOf(TextRange range)
        => new(range.Text, RichTextMap.SaveRtf(range), RichTextMap.Save(range));

    private sealed class Hookup
    {
        private readonly RichTextBox _box;
        private EditorViewModel? _vm;
        private bool _loading;
        private bool _syncPending;

        public RichTextMap? Map { get; private set; }

        public Hookup(RichTextBox box)
        {
            _box = box;
            RichFormat.Attach(box);
            RichTable.Attach(box);
            foreach (var gesture in BlockedGestures)
                box.InputBindings.Add(new KeyBinding(ApplicationCommands.NotACommand, gesture));

            DataObject.AddPastingHandler(box, OnPasting);
            box.TextChanged += OnTextChanged;
            box.DataContextChanged += (_, e) => Bind(e.NewValue as EditorViewModel);
            Bind(box.DataContext as EditorViewModel);
        }

        private void Bind(EditorViewModel? vm)
        {
            if (ReferenceEquals(_vm, vm)) return;

            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.CaptureBody = null;
                _vm.CaptureAllForCopy = null;
            }

            _vm = vm;
            if (_vm is null) return;

            _vm.PropertyChanged += OnVmChanged;
            _vm.CaptureBody = Capture;
            _vm.CaptureAllForCopy = () => PayloadOf(new TextRange(_box.Document.ContentStart, _box.Document.ContentEnd));
            Reload();
        }

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

                _box.Document = document;
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
                    e.FormatToApply = DataFormats.XamlPackage;
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
