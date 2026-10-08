using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using TextBean.Services;
using TextBean.Services.Interfaces;
using TextBean.ViewModels;
using TextBean.Views.Platform;
using static TextBean.Tests.WpfTestHost;

namespace TextBean.Tests;

/// <summary>
/// 음성 입력 조율 (D-180 · D-184 · D-186 · D-188). 실제 MainWindow 를 화면 밖에 띄우고 실제 본문(RichTextBox · TextBox)에 넣는다.
/// 마이크 · 모델은 가짜 — 실제 녹음은 하지 않는다(plan R-8). 소리는 「말 1초 + 쉼 1초」= 조각 하나.
/// </summary>
internal sealed class DictationScene : IDisposable
{
    public TabScene Tabs { get; }
    public FakeRecorder Recorder { get; } = new();
    public FakeSpeechToText Speech { get; } = new();
    public AppSettingsService Settings { get; }
    public DictationController Dictation { get; }

    public DictationScene(params string[] titles)
    {
        Tabs = TabScene.Open(titles);
        try
        {
            Settings = new AppSettingsService(Path.Combine(Tabs.Vault.Root, "voice-settings.json"));
            Wait(Settings.LoadAsync());
            Settings.Current.VoiceModelPath = FakeSpeechToText.GoodPath;
            Dictation = new DictationController(Recorder, Speech, Settings, Tabs.Dialogs, Tabs.Window.FindBodyFor, () => Tabs.Vault.Root);
        }
        catch
        {
            Tabs.Dispose();
            throw;
        }
    }

    public FakeDialogs Dialogs => Tabs.Dialogs;
    public EditorViewModel Tab(int index) => Tabs.Shell.Tabs[index];
    public RichTextBox Rich(int index) => (RichTextBox)Tabs.Window.FindBodyFor(Tab(index))!;
    public TextBox Plain(int index) => (TextBox)Tabs.Window.FindBodyFor(Tab(index))!;

    /// <summary>
    /// 보고 있는 탭에서 시작한다(실제 사용과 같다). 한 번도 보인 적 없는 탭은 본문 템플릿이 아직 펼쳐지지 않아 본문이 없다 —
    /// 가려진 Collapsed 칸은 배치를 건너뛴다 [실측 — 첫 실행에서 시작이 안 됐다]. 한 번 보인 본문은 가려져도 남는다.
    /// </summary>
    public void Start(int index)
    {
        if (!ReferenceEquals(Tabs.Shell.ActiveTab, Tab(index)))
        {
            Tabs.Shell.ActiveTab = Tab(index);
            Pump();
        }

        Wait(Dictation.StartAsync(Tab(index)));
    }

    /// 말 하나(조각 하나가 생긴다). 20초 미만의 말은 3초를 쉬어야 잘린다 (D-194 회의 모드).
    public void Say(double seconds = 2) => Recorder.Push(Sound.Speech(seconds, pauseAfter: 3.5));

    public void PumpUntil(Func<bool> done, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!done())
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(what);
            Pump(DispatcherPriority.Background);
            Thread.Sleep(5);
        }
        Pump();
    }

    public void Settled() => PumpUntil(() => Dictation.State == DictationState.Idle || Dictation.Pending == 0 && !Dictation.IsPreparing, "받아쓰기가 끝나지 않았다");

    public void Dispose()
    {
        try { Dictation?.Dispose(); }
        finally { Tabs.Dispose(); }
    }
}

/// 시험 소리 — 0.4초 소리 + 0.1초 끊김의 되풀이(SpeechChunkerTests 와 같은 모양).
internal static class Sound
{
    public static float[] Speech(double seconds, double pauseAfter)
    {
        var speech = new float[(int)(seconds * SpeechChunker.SampleRate)];
        for (var i = 0; i < speech.Length; i++)
            speech[i] = i % 8000 < 6400 ? 0.3f * (float)Math.Sin(2 * Math.PI * 220 * i / SpeechChunker.SampleRate) : 0f;
        return [.. speech, .. new float[(int)(pauseAfter * SpeechChunker.SampleRate)]];
    }
}

[Collection(WpfScreenCollection.Name)]
public class DictationTests
{
    [Fact]
    public void 시작_자리에_조각이_차례로_띄어_들어가고_커서가_따라간다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);
        SetRich(box, "앞글자 뒤글자", caret: 3);
        scene.Speech.Texts.Enqueue("첫째");
        scene.Speech.Texts.Enqueue("둘째");

        scene.Start(0);
        scene.Say();
        scene.Say();
        scene.PumpUntil(() => RichText(box).Contains("둘째"), "두 조각이 들어가지 않았다");

        Assert.Equal("앞글자 첫째 둘째 뒤글자", RichText(box));
        Assert.Equal(9, Offset(box, box.CaretPosition));
    });

    [Fact]
    public void 사용자_커서와_선택은_옮기지_않는다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);
        SetRich(box, "가나다라마바", caret: 2);

        scene.Start(0);
        box.Selection.Select(At(box, 4), At(box, 6));
        scene.Say();
        scene.PumpUntil(() => RichText(box).Contains("조각"), "들어가지 않았다");

        Assert.Equal("가나 조각1다라마바", RichText(box));
        Assert.Equal("마바", box.Selection.Text);
    });

    [Fact]
    public void 선택이_있으면_선택_끝에_넣고_선택_글자는_지우지_않는다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);
        SetRich(box, "가나다라", caret: 0);
        box.Selection.Select(At(box, 1), At(box, 3));

        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => RichText(box).Contains("조각"), "들어가지 않았다");

        Assert.Equal("가나다 조각1라", RichText(box));
        Assert.Equal("나다", box.Selection.Text);
    });

    [Fact]
    public void 앞쪽을_고쳐도_넣을_자리가_따라간다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);
        SetRich(box, "가나다", caret: 3);

        scene.Start(0);
        box.Selection.Select(At(box, 0), At(box, 0));
        box.Selection.Text = "앞에 ";
        box.Selection.Select(At(box, 0), At(box, 0));
        scene.Say();
        scene.PumpUntil(() => RichText(box).Contains("조각"), "들어가지 않았다");

        Assert.Equal("앞에 가나다 조각1", RichText(box));
    });

    [Fact]
    public void 조각_하나는_실행취소_한_번에_되돌아간다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);
        SetRich(box, "가", caret: 1);

        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => RichText(box).Contains("조각1"), "첫 조각");
        scene.Say();
        scene.PumpUntil(() => RichText(box).Contains("조각2"), "둘째 조각");

        box.Undo();
        Assert.Equal("가 조각1", RichText(box));
        box.Undo();
        Assert.Equal("가", RichText(box));
    });

    [Fact]
    public void 빈_문서에는_띄지_않고_넣는다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);

        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => RichText(box).Contains("조각"), "들어가지 않았다");

        Assert.Equal("조각1", RichText(box));
    });

    [Fact]
    public void 표_칸_안에_넣는다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);
        var cell = new TableCell(new Paragraph(new Run("칸")));
        var row = new TableRow();
        row.Cells.Add(cell);
        row.Cells.Add(new TableCell(new Paragraph(new Run("옆"))));
        var group = new TableRowGroup();
        group.Rows.Add(row);
        var table = new Table();
        table.RowGroups.Add(group);
        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(table);
        var end = ((Paragraph)cell.Blocks.FirstBlock).ContentEnd;
        box.Selection.Select(end, end);

        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => RichText(box).Contains("조각"), "들어가지 않았다");

        Assert.Equal("칸 조각1", new TextRange(cell.ContentStart, cell.ContentEnd).Text.TrimEnd());
    });

    [Fact]
    public void 다른_탭으로_가도_시작한_탭에_넣는다() => Run(() =>
    {
        using var scene = new DictationScene("첫째", "둘째");
        scene.Tabs.Shell.ActiveTab = scene.Tab(0);
        Pump();

        scene.Start(0);
        scene.Tabs.Shell.ActiveTab = scene.Tab(1);
        Pump();
        scene.Say();
        scene.PumpUntil(() => RichText(scene.Rich(0)).Contains("조각"), "들어가지 않았다");

        Assert.Equal("조각1", RichText(scene.Rich(0)));
        Assert.Equal("", RichText(scene.Rich(1)));
    });

    [Fact]
    public void 모델_준비_중에_생긴_조각은_기다렸다가_순서대로_넣는다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);
        scene.Speech.PrepareGate = new TaskCompletionSource();

        scene.Start(0);
        Assert.Equal(1, scene.Recorder.StartCount);          // 준비를 기다리지 않고 바로 듣는다 (plan R-4)
        scene.Say();
        scene.Say();
        scene.PumpUntil(() => scene.Dictation.Pending == 2, "조각 둘이 쌓이지 않았다");
        Assert.True(scene.Dictation.IsPreparing);
        Assert.Equal("", RichText(box));

        scene.Speech.PrepareGate.SetResult();
        scene.PumpUntil(() => RichText(box).Contains("조각2"), "들어가지 않았다");

        Assert.Equal("조각1 조각2", RichText(box));
        Assert.False(scene.Dictation.IsPreparing);
    });

    [Fact]
    public void 대상_탭이_닫히면_남은_조각을_버리고_쉰다() => Run(() =>
    {
        using var scene = new DictationScene("첫째", "둘째");
        scene.Speech.TranscribeGate = new TaskCompletionSource();

        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => scene.Dictation.Pending == 1, "조각이 쌓이지 않았다");
        Wait(scene.Tabs.Shell.CloseTabAsync(scene.Tab(0)));

        Assert.Equal(DictationState.Idle, scene.Dictation.State);
        Assert.False(scene.Recorder.IsRecording);

        scene.Speech.TranscribeGate.SetResult();
        Pump();
        Thread.Sleep(50);
        Pump();
        Assert.Equal("", RichText(scene.Rich(0)));                 // 남은 탭(둘째)에 새지 않는다
    });

    [Fact]
    public void 읽기_전용이_된_탭에는_넣지_않고_멈춘다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);
        scene.Speech.TranscribeGate = new TaskCompletionSource();

        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => scene.Dictation.Pending == 1, "조각이 쌓이지 않았다");
        typeof(EditorViewModel).GetProperty(nameof(EditorViewModel.IsReadOnly))!.SetValue(scene.Tab(0), true);
        scene.Speech.TranscribeGate.SetResult();
        scene.PumpUntil(() => scene.Dictation.State == DictationState.Idle, "멈추지 않았다");

        Assert.Equal("", RichText(box));
        Assert.False(scene.Dictation.CanStart(scene.Tab(0)));
    });

    [Fact]
    public void 취소하면_받아쓰던_조각까지_버린다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);
        scene.Speech.TranscribeGate = new TaskCompletionSource();

        scene.Start(0);
        scene.Say();
        scene.Say();
        scene.PumpUntil(() => scene.Dictation.Pending == 2, "조각이 쌓이지 않았다");
        scene.Dictation.Cancel();

        Assert.Equal(DictationState.Idle, scene.Dictation.State);
        Assert.False(scene.Recorder.IsRecording);
        scene.Speech.TranscribeGate.SetResult();
        Pump();
        Thread.Sleep(50);
        Pump();
        Assert.Equal("", RichText(box));
    });

    [Fact]
    public void 멈추면_말하던_조각까지_넣고_쉼으로_돌아간다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);

        scene.Start(0);
        scene.Recorder.Push(Sound.Speech(1, pauseAfter: 0));    // 아직 쉬지 않았다 — 조각이 안 생겼다
        Pump();
        Assert.Equal(0, scene.Dictation.Pending);

        scene.Dictation.Stop();
        Assert.False(scene.Recorder.IsRecording);
        Assert.Equal(DictationState.Finishing, scene.Dictation.State);
        scene.PumpUntil(() => scene.Dictation.State == DictationState.Idle, "쉼으로 돌아가지 않았다");

        Assert.Equal("조각1", RichText(box));
    });

    [Fact]
    public void 마이크가_없으면_오류_창_한_번_뒤_쉰다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        scene.Recorder.StartError = new VoiceUnavailableException("마이크가 없습니다. 마이크를 연결한 뒤 다시 누르세요.");

        scene.Start(0);

        Assert.Equal(1, scene.Dialogs.ErrorCount);
        Assert.Contains("마이크가 없습니다", scene.Dialogs.LastErrorMessage);
        Assert.Equal(DictationState.Idle, scene.Dictation.State);
        Assert.Equal(0, scene.Speech.PrepareCount);
    });

    [Fact]
    public void 모델_경로가_없으면_고르게_하고_고른_것을_저장한다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        scene.Settings.Current.VoiceModelPath = null;

        // 취소하면 조용히 그만
        scene.Start(0);
        Assert.Equal(1, scene.Dialogs.PickModelFileCount);
        Assert.Equal(DictationState.Idle, scene.Dictation.State);
        Assert.Equal(0, scene.Dialogs.ErrorCount);

        // 다른 파일을 고르면 이유를 알린다
        scene.Dialogs.PickModelFileResult = @"C:\다른\파일.bin";
        scene.Start(0);
        Assert.Equal(1, scene.Dialogs.ErrorCount);
        Assert.Null(scene.Settings.Current.VoiceModelPath);

        scene.Dialogs.PickModelFileResult = FakeSpeechToText.GoodPath;
        scene.Start(0);
        Assert.Equal(FakeSpeechToText.GoodPath, scene.Settings.Current.VoiceModelPath);
        Assert.Equal(DictationState.Listening, scene.Dictation.State);
        scene.PumpUntil(() => scene.Speech.PreparedPath is not null, "준비하지 않았다");
        Assert.Equal(FakeSpeechToText.GoodPath, scene.Speech.PreparedPath);
    });

    [Fact]
    public void 모델_준비에_실패하면_오류_창_뒤_쉬고_아무것도_넣지_않는다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        scene.Speech.PrepareGate = new TaskCompletionSource();
        scene.Speech.PrepareError = new VoiceUnavailableException("모델 파일 내용이 다릅니다.");

        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => scene.Dictation.Pending == 1, "조각이 쌓이지 않았다");
        scene.Speech.PrepareGate.SetResult();
        scene.PumpUntil(() => scene.Dictation.State == DictationState.Idle, "멈추지 않았다");

        Assert.Equal(1, scene.Dialogs.ErrorCount);
        Assert.Contains("내용이 다릅니다", scene.Dialogs.LastErrorMessage);
        Assert.False(scene.Recorder.IsRecording);
        Assert.Equal("", RichText(scene.Rich(0)));
    });

    [Fact]
    public void 받아쓰기에_실패하면_오류_창_뒤_쉰다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        scene.Speech.TranscribeError = new InvalidOperationException("엔진 내부 오류");

        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => scene.Dictation.State == DictationState.Idle, "멈추지 않았다");

        Assert.Equal(1, scene.Dialogs.ErrorCount);
        Assert.DoesNotContain("엔진 내부 오류", scene.Dialogs.LastErrorMessage);   // 예외 문구를 그대로 보이지 않는다
        Assert.False(scene.Recorder.IsRecording);
    });

    [Fact]
    public void 녹음_장치가_빠지면_쌓인_조각은_넣고_알린다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var box = scene.Rich(0);
        scene.Speech.TranscribeGate = new TaskCompletionSource();

        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => scene.Dictation.Pending == 1, "조각이 쌓이지 않았다");
        scene.Recorder.Fail("마이크 연결이 끊겼습니다.");
        scene.PumpUntil(() => scene.Dialogs.ErrorCount == 1, "알리지 않았다");
        Assert.Equal(DictationState.Finishing, scene.Dictation.State);

        scene.Speech.TranscribeGate.SetResult();
        scene.PumpUntil(() => scene.Dictation.State == DictationState.Idle, "쉼으로 돌아가지 않았다");
        Assert.Equal("조각1", RichText(box));
    });

    [Fact]
    public void 넘겨받은_소리_배열은_지운다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        scene.Start(0);

        var samples = Sound.Speech(1, pauseAfter: 1);
        scene.Recorder.Push(samples);

        Assert.All(samples, s => Assert.Equal(0f, s));
    });

    [Fact]
    public void 평문_본문_자리_커서_선택_앞쪽_고침_실행취소() => Run(() =>
    {
        using var scene = new DictationScene("평문.txt");
        var box = scene.Plain(0);
        box.Text = "가나다라";
        box.Select(2, 0);

        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => box.Text.Contains("조각1"), "첫 조각");
        Assert.Equal("가나 조각1다라", box.Text);
        Assert.Equal(6, box.CaretIndex);                      // 커서가 자리에 있었으니 따라왔다(2 + 「 조각1」 4)

        // 사용자가 끝을 고르고, 앞에 글자를 넣는다 — 자리는 따라가고 선택은 그 글자 그대로
        box.Select(0, 0);
        box.SelectedText = "앞";
        box.Select(box.Text.Length - 2, 2);
        scene.Say();
        scene.PumpUntil(() => box.Text.Contains("조각2"), "둘째 조각");

        Assert.Equal("앞가나 조각1 조각2다라", box.Text);
        Assert.Equal("다라", box.SelectedText);

        box.Undo();
        Assert.Equal("앞가나 조각1다라", box.Text);
    });

    [Fact]
    public void 해제하면_마이크와_모델을_닫는다() => Run(() =>
    {
        var scene = new DictationScene("메모");
        try
        {
            scene.Start(0);
            scene.Dictation.Dispose();

            Assert.True(scene.Recorder.Disposed);
            Assert.True(scene.Speech.Disposed);
            Assert.Equal(DictationState.Idle, scene.Dictation.State);
            Assert.False(scene.Dictation.CanStart(scene.Tab(0)));
        }
        finally
        {
            scene.Dispose();
        }
    });

    [Fact]
    public void 단추_메뉴_단축키가_같은_명령이고_글자와_상태_줄이_따라간다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var shell = scene.Tabs.Shell;
        shell.UseDictation(scene.Dictation);
        scene.Speech.PrepareGate = new TaskCompletionSource();
        shell.ActiveTab = scene.Tab(0);
        Pump();

        Assert.Same(shell.ToggleDictationCommand, shell.CommandFor("voiceInput"));
        Assert.Null(ShortcutCatalog.Find("voiceInput")!.DefaultKey);                       // 기본 키 없음 — 사용자가 정한다
        var topMenu = (Menu)scene.Tabs.Window.FindName("TopMenu");
        var edit = topMenu.Items.OfType<MenuItem>().Single(m => m.Header as string == "편집");
        var menu = edit.Items.OfType<MenuItem>().Single(m => ReferenceEquals(m.Command, shell.ToggleDictationCommand));
        var button = FindDescendants<Button>(scene.Tabs.Window).Single(b => ReferenceEquals(b.Command, shell.ToggleDictationCommand));
        Assert.Equal("음성 입력 시작", menu.Header);
        Assert.Equal("말하기", button.Content);
        Assert.True(button.IsEnabled);
        Assert.Equal("", shell.DictationStatusText);

        button.Command.Execute(null);
        Wait(shell.Pending);
        Pump();
        Assert.Equal(DictationState.Listening, scene.Dictation.State);
        Assert.Equal("음성 입력 멈추기", menu.Header);
        Assert.Equal("● 멈추기", button.Content);
        Assert.StartsWith("● 듣는 중 0:0", shell.DictationStatusText);
        Assert.Contains("「메모」", shell.DictationStatusText);
        Assert.Contains("준비 중", shell.DictationStatusText);

        scene.Say();
        scene.PumpUntil(() => shell.DictationStatusText.Contains("받아쓰는 중 1"), "상태 줄에 조각 수가 안 보인다");
        Snapshot(scene.Tabs.Window, "voice-listening");                                    // TEXTBEAN_TEST_PNG 가 있을 때만

        // 다른 입구(메뉴 명령)로 누르면 멈춘다
        menu.Command.Execute(null);
        Wait(shell.Pending);
        Assert.Equal(DictationState.Finishing, scene.Dictation.State);
        Assert.StartsWith("마무리 중", shell.DictationStatusText);
        Assert.Equal("음성 입력 시작", menu.Header);

        scene.Speech.PrepareGate.SetResult();
        scene.PumpUntil(() => scene.Dictation.State == DictationState.Idle, "쉼으로 돌아가지 않았다");
        Assert.Equal("", shell.DictationStatusText);
        Assert.Equal("조각1", RichText(scene.Rich(0)));
    });

    [Theory]
    [InlineData(double.NegativeInfinity, "░░░░░")]
    [InlineData(-70.0, "░░░░░")]
    [InlineData(-52.0, "█░░░░")]          // 노트북 마이크 작은 말(16비트 약 100)
    [InlineData(-40.0, "██░░░")]
    [InlineData(-35.0, "███░░")]          // 노트북 마이크 보통 말(약 600)
    [InlineData(-20.0, "█████")]
    [InlineData(0.0, "█████")]
    public void 입력_크기_막대는_8dB_마다_한_칸(double decibels, string expected)
        => Assert.Equal(expected, ShellViewModel.LevelBars(decibels));

    [Fact]
    public void 듣는_동안_상태_줄_막대가_소리를_따라간다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var shell = scene.Tabs.Shell;
        shell.UseDictation(scene.Dictation);
        scene.Speech.TranscribeGate = new TaskCompletionSource();
        scene.Start(0);

        scene.Recorder.Push(Sound.Speech(0.3, pauseAfter: 0));                 // 큰 소리(약 -13dB)
        scene.PumpUntil(() => shell.DictationStatusText.Contains("█████"), "막대가 차지 않았다");
        Snapshot(scene.Tabs.Window, "voice-meter-loud");                        // TEXTBEAN_TEST_PNG 가 있을 때만
        Assert.True(scene.Dictation.InputDecibels > -20);

        scene.Recorder.Push(new float[1600]);                                   // 무음
        scene.PumpUntil(() => shell.DictationStatusText.Contains("░░░░░"), "막대가 비지 않았다");

        scene.Dictation.Cancel();
        Assert.True(double.IsNegativeInfinity(scene.Dictation.InputDecibels));   // 쉬면 막대 없음
        Assert.Equal("", shell.DictationStatusText);
    });

    [Fact]
    public void 붙지_않았거나_문서가_없으면_눌러도_아무것도_하지_않는다() => Run(() =>
    {
        using var scene = new DictationScene("메모");
        var shell = scene.Tabs.Shell;

        shell.ToggleDictationCommand.Execute(null);
        Wait(shell.Pending);
        Assert.Equal(0, scene.Recorder.StartCount);

        shell.UseDictation(scene.Dictation);
        shell.ActiveTab = null;
        shell.ToggleDictationCommand.Execute(null);
        Wait(shell.Pending);
        Assert.Equal(0, scene.Recorder.StartCount);
    });

    /// 잠그기 · 절전 · 금고 바꾸기 · 종료 · Windows 종료 → 버림(D-186), 트레이 숨김 → 듣기만 멈추고 남은 조각까지(D-185).
    [Theory]
    [InlineData("잠그기", false)]
    [InlineData("절전", false)]
    [InlineData("금고 바꾸기", false)]
    [InlineData("종료", false)]
    [InlineData("Windows 종료", false)]
    [InlineData("숨기기", true)]
    public void 셸이_멈추거나_버린다(string path, bool keepsPending) => Run(() =>
    {
        using var scene = new DictationScene("메모.txt", "금고 문서");
        using var other = new TempVault();
        var shell = scene.Tabs.Shell;
        shell.UseDictation(scene.Dictation);
        scene.Speech.TranscribeGate = new TaskCompletionSource();
        scene.Dialogs.PickFolderResult = other.Root;

        // .txt — 잠가도 열려 있는 탭. 금고를 바꾸면 탭이 닫히니 본문을 먼저 잡아 둔다
        shell.ActiveTab = scene.Tab(0);
        Pump();
        var box = scene.Plain(0);
        box.Select(box.Text.Length, 0);
        scene.Start(0);
        scene.Say();
        scene.PumpUntil(() => scene.Dictation.Pending == 1, "조각이 쌓이지 않았다");

        switch (path)
        {
            case "잠그기": shell.LockCommand.Execute(null); Wait(shell.Pending); break;
            case "절전": Wait(shell.LockQuietlyAsync()); break;
            case "금고 바꾸기": shell.ChangeRootCommand.Execute(null); Wait(shell.Pending); break;
            case "종료": Wait(shell.SaveAllForExitAsync()); break;
            case "Windows 종료": Wait(shell.SaveAllQuietlyAsync()); break;
            case "숨기기": Wait(shell.PrepareHideAsync()); break;
        }

        Assert.False(scene.Recorder.IsRecording);
        Assert.Equal(keepsPending ? DictationState.Finishing : DictationState.Idle, scene.Dictation.State);

        scene.Speech.TranscribeGate.SetResult();
        if (keepsPending) scene.PumpUntil(() => scene.Dictation.State == DictationState.Idle, "남은 조각을 넣지 않았다");
        else { Pump(); Thread.Sleep(50); Pump(); }

        Assert.Equal(keepsPending ? "평문 조각1" : "평문", box.Text);
    });

    internal static string RichText(RichTextBox box)
        => new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.TrimEnd('\r', '\n');

    internal static void SetRich(RichTextBox box, string text, int caret)
    {
        box.Document.Blocks.Clear();
        box.Document.Blocks.Add(new Paragraph(new Run(text)));
        var at = At(box, caret);
        box.Selection.Select(at, at);
    }

    /// 글자 위치(한 문단) → TextPointer.
    internal static TextPointer At(RichTextBox box, int index)
    {
        var count = 0;
        for (var p = box.Document.ContentStart; p is not null; p = p.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (p.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text) continue;
            var run = p.GetTextInRun(LogicalDirection.Forward);
            if (count + run.Length >= index) return p.GetPositionAtOffset(index - count)!;
            count += run.Length;
        }
        return box.Document.ContentEnd;
    }

    internal static int Offset(RichTextBox box, TextPointer at)
        => new TextRange(box.Document.ContentStart, at).Text.Length;
}
