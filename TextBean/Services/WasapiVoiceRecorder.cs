using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;
using TextBean.Services.Interfaces;

namespace TextBean.Services;

/// <summary>
/// NAudio WASAPI 녹음기(<see cref="WasapiRecorderBuilder"/> — 3.x 의 새 길, 옛 WasapiCapture 는 사용 중단)로 기본 녹음 장치를 공유 모드로
/// 열어 16kHz 모노로 바꿔 넘긴다 (D-179). 장치는 보통 48kHz 실수 스테레오로 준다 — 채널을 섞고 NAudio 의 관리 코드 리샘플러로 줄인다.
/// NAudio 의 COM · 네이티브 호출은 라이브러리 자체 몫이다(D-183). 소리는 메모리에만 둔다 (D-188).
/// </summary>
public sealed class WasapiVoiceRecorder : IVoiceRecorder
{
    private const int NotFound = unchecked((int)0x80070490);          // 녹음 장치 없음 [실측 — research 0-1절]
    private const int AccessDenied = unchecked((int)0x80070005);      // 개인 정보 설정에서 마이크 막힘
    private const int DeviceInvalidated = unchecked((int)0x88890004); // AUDCLNT_E_DEVICE_INVALIDATED — 녹음 중 빠짐
    private const int DeviceInUse = unchecked((int)0x8889000A);       // AUDCLNT_E_DEVICE_IN_USE — 다른 프로그램이 독점

    /// 장치가 준 바이트를 16kHz 모노 실수로.
    public delegate float[] SampleConverter(ReadOnlySpan<byte> data);

    private readonly Lock _gate = new();
    private WasapiRecorder? _recorder;
    private SampleConverter? _convert;
    private bool _stopping;

    public event Action<float[]>? Samples;
    public event Action<VoiceUnavailableException>? Failed;

    public void Start()
    {
        lock (_gate)
        {
            if (_recorder is not null) throw new InvalidOperationException("이미 듣고 있습니다.");

            WasapiRecorder? recorder = null;
            try
            {
                recorder = new WasapiRecorderBuilder().WithSharedMode().Build();
                _convert = CreateConverter(recorder.WaveFormat);
                recorder.DataAvailable += OnDataAvailable;
                recorder.RecordingStopped += OnRecordingStopped;
                _stopping = false;
                _recorder = recorder;
                recorder.StartRecording();
            }
            catch (Exception ex) when (ex is not VoiceUnavailableException)
            {
                _recorder = null;
                recorder?.Dispose();
                AppLog.Warn("voice-mic-start", null, ex);
                throw new VoiceUnavailableException(Describe(ex), ex);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_recorder is null) return;
            _stopping = true;
            _recorder.StopRecording();
        }
    }

    public void Dispose()
    {
        WasapiRecorder? recorder;
        lock (_gate)
        {
            recorder = _recorder;
            _recorder = null;
            _stopping = true;
        }

        if (recorder is null) return;
        recorder.DataAvailable -= OnDataAvailable;
        recorder.RecordingStopped -= OnRecordingStopped;
        recorder.Dispose();
    }

    /// <summary>
    /// 녹음 장치 오류를 사용자 문구로(안쪽 예외까지 본다). 시험은 이것만 본다 — 실제 녹음을 시험에서 시작하지 않는다
    /// (마이크를 꽂아 두면 시험이 녹음하게 된다, plan R-8).
    /// </summary>
    public static string Describe(Exception error)
    {
        for (var at = error; at is not null; at = at.InnerException)
        {
            var text = at.HResult switch
            {
                NotFound => "마이크가 없습니다. 마이크를 연결한 뒤 다시 누르세요.",
                AccessDenied => "마이크를 쓸 수 없습니다. Windows 설정 › 개인 정보 및 보안 › 마이크에서 「데스크톱 앱이 마이크에 액세스하도록 허용」을 켜세요.",
                DeviceInvalidated => "마이크 연결이 끊겼습니다.",
                DeviceInUse => "다른 프로그램이 마이크를 혼자 쓰고 있습니다. 그 프로그램을 닫은 뒤 다시 누르세요.",
                _ => null
            };
            if (text is not null) return text;
        }

        return "마이크를 열 수 없습니다.";
    }

    /// <summary>
    /// 장치가 주는 형식(실수 32비트 · 정수 16/24/32비트, 채널 수, 표본 빈도)을 16kHz 모노 실수로 바꾸는 함수를 만든다.
    /// 리샘플러는 앞 조각의 상태를 이어 쓴다 — 한 녹음에 하나.
    /// </summary>
    public static SampleConverter CreateConverter(WaveFormat format)
    {
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                   || format is WaveFormatExtensible { SubFormat: var sub } && sub == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        var bits = format.BitsPerSample;
        var channels = format.Channels;
        var bytesPerSample = bits / 8;
        var kind = (isFloat, bits) switch
        {
            (true, 32) => SampleKind.Float32,
            (false, 16) => SampleKind.Int16,
            (false, 24) => SampleKind.Int24,
            (false, 32) => SampleKind.Int32,
            _ => throw new VoiceUnavailableException("이 마이크의 소리 형식은 쓸 수 없습니다.")
        };

        WdlResampler? resampler = null;
        if (format.SampleRate != SpeechChunker.SampleRate)
        {
            resampler = new WdlResampler();
            resampler.SetMode(true, 2, false);
            resampler.SetFilterParms();
            resampler.SetFeedMode(true);
            resampler.SetRates(format.SampleRate, SpeechChunker.SampleRate);
        }

        return data =>
        {
            var frames = data.Length / (bytesPerSample * channels);
            var mono = new float[frames];
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                for (var c = 0; c < channels; c++) sum += Read(kind, data[((f * channels + c) * bytesPerSample)..]);
                mono[f] = sum / channels;
            }

            if (resampler is null || frames == 0) return mono;

            var wanted = resampler.ResamplePrepare(frames, 1, out var input);
            mono.AsSpan(0, wanted).CopyTo(input);
            Array.Clear(mono);

            var output = new float[(int)((long)frames * SpeechChunker.SampleRate / format.SampleRate) + 16];
            var produced = resampler.ResampleOut(output, wanted, output.Length, 1);
            return produced == output.Length ? output : output[..produced];
        };
    }

    private enum SampleKind { Float32, Int16, Int24, Int32 }

    private static float Read(SampleKind kind, ReadOnlySpan<byte> at) => kind switch
    {
        SampleKind.Float32 => BitConverter.ToSingle(at),
        SampleKind.Int16 => BitConverter.ToInt16(at) / 32768f,
        SampleKind.Int24 => ((at[0] << 8 | at[1] << 16 | at[2] << 24) >> 8) / 8388608f,
        _ => BitConverter.ToInt32(at) / 2147483648f
    };

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (buffer.IsEmpty || _convert is not { } convert) return;
        Samples?.Invoke(convert(buffer));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        bool stopping;
        WasapiRecorder? ended = null;
        lock (_gate)
        {
            stopping = _stopping;
            if (sender is WasapiRecorder recorder && ReferenceEquals(recorder, _recorder))
            {
                ended = recorder;
                _recorder = null;
            }
        }

        // 이 알림은 녹음 스레드에서 올 수 있다 — 거기서 해제하면 해제가 녹음 스레드 끝을 기다리며 자기 자신을 기다린다
        if (ended is not null)
        {
            ended.DataAvailable -= OnDataAvailable;
            ended.RecordingStopped -= OnRecordingStopped;
            _ = Task.Run(ended.Dispose);
        }

        if (stopping) return;

        AppLog.Warn("voice-mic-stopped", null, e.Exception);
        Failed?.Invoke(new VoiceUnavailableException(
            e.Exception is null ? "마이크 녹음이 멈췄습니다." : Describe(e.Exception), e.Exception));
    }
}
