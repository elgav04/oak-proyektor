using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App.Audio;
using OakProyektor.Core.Audio;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.Tests.Playback;

/// <summary>
/// El motor de pistas con un respaldo falso y un enumerador de dispositivos SIMULADO: ids estables, nunca saltar a otro dispositivo, pérdida y regreso del mismo, volumen maestro, fundidos y
/// «Silencio» inmediato aunque una apertura de dispositivo esté colgada. No toca NAudio ni ningún dispositivo real.
/// </summary>
public class AudioEngineTests : IDisposable
{
    private static readonly AudioDeviceInfo Laptop = new("{0.0.0.00000000}.laptop-speakers", "Altavoces del portátil");
    private static readonly AudioDeviceInfo Usb = new("{0.0.0.00000000}.usb-mixer", "Interfaz USB (mezcladora)");
    private static readonly AudioDeviceInfo Aux = new("{0.0.0.00000000}.analog-aux", "Salida auxiliar de la PC");

    private readonly FakeAudioBackend _backend = new();
    private readonly FakeDeviceEnumerator _devices;
    private AudioSettings _settings = new();
    private readonly AudioEngine _engine;

    public AudioEngineTests()
    {
        _devices = new FakeDeviceEnumerator(Laptop, Usb, Aux);   // el primero es el predeterminado de Windows
        _engine = new AudioEngine(_backend, _devices, () => _settings, NullLogger.Instance);
    }

    public void Dispose() => _engine.Dispose();

    private void Play(bool start = true, long order = 1, int fadeIn = 500, int clip = 100, bool loop = false) =>
        _engine.Play(7, @"C:\datos\media\files\ab\abc.mp3", 60_000, loop, start, clip, fadeIn, order);

    private static void WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { if (DateTime.UtcNow > until) Assert.Fail("la condición no se cumplió a tiempo"); Thread.Sleep(5); }
    }

    // ───────────── dispositivo ─────────────

    [Fact]
    public void Without_a_church_device_the_track_uses_the_windows_default()
    {
        Play();
        Assert.Null(_backend.Opened.Single().DeviceId);   // nulo = el predeterminado de Windows
        Assert.Equal(AudioTrackPhase.Playing, _engine.Snapshot()!.Phase);
    }

    [Fact]
    public void With_a_church_device_the_track_uses_exactly_that_id_even_when_windows_default_is_another()
    {
        _settings = _settings with { ChurchDeviceId = Usb.Id, ChurchDeviceName = Usb.Name };
        Play();
        Assert.Equal(Usb.Id, _backend.Opened.Single().DeviceId);   // el predeterminado es Laptop: no se usa
    }

    [Fact]
    public void If_the_church_device_is_not_connected_nothing_plays_and_it_never_falls_back_to_another_device()
    {
        _settings = _settings with { ChurchDeviceId = "{0.0.0.00000000}.otro-que-no-esta" };
        var ex = Assert.Throws<AudioException>(() => Play());
        Assert.Equal(AudioErrorCodes.DeviceNotFound, ex.Code);
        Assert.Empty(_backend.Opened);   // ni los altavoces del portátil ni el predeterminado
        Assert.Null(_engine.Snapshot());
    }

    [Fact]
    public void A_device_that_disappears_pauses_the_track_keeps_its_position_and_does_NOT_move_to_another_device()
    {
        _settings = _settings with { ChurchDeviceId = Usb.Id };
        Play();
        var pb = _backend.Playbacks[0];
        pb.PositionMs = 12_345;

        _devices.Disconnect(Usb.Id);   // Windows pasa el predeterminado a otro; el motor NO lo sigue

        var snap = _engine.Snapshot()!;
        Assert.Equal((AudioTrackPhase.DeviceLost, 12_345L), (snap.Phase, snap.PositionMs));
        Assert.Equal(AudioErrorCodes.DeviceLost, snap.ErrorCode);
        WaitFor(() => pb.Disposed);
        Assert.Single(_backend.Opened);   // sigue siendo UNA sola apertura: no se abrió ningún otro dispositivo
    }

    [Fact]
    public void Resuming_without_the_church_device_connected_fails_and_with_it_back_the_operator_resumes_where_it_stopped()
    {
        _settings = _settings with { ChurchDeviceId = Usb.Id };
        Play();
        _backend.Playbacks[0].PositionMs = 9_000;
        _devices.Disconnect(Usb.Id);

        Assert.Equal(AudioErrorCodes.DeviceNotFound, Assert.Throws<AudioException>(() => _engine.Resume(2, 300)).Code);
        Assert.Single(_backend.Opened);

        _devices.Connect(Usb);   // vuelve el MISMO dispositivo, pero el ajuste «reanudar solo» está apagado
        Assert.Equal(AudioTrackPhase.DeviceLost, _engine.Snapshot()!.Phase);
        Assert.Single(_backend.Opened);

        _engine.Resume(3, 300);   // el operador decide
        Assert.Equal(2, _backend.Opened.Count);
        Assert.Equal(Usb.Id, _backend.Opened[1].DeviceId);
        Assert.Contains("seek:9000", _backend.Playbacks[1].Calls);
        Assert.Contains("play:300", _backend.Playbacks[1].Calls);
        Assert.Equal(AudioTrackPhase.Playing, _engine.Snapshot()!.Phase);
    }

    [Fact]
    public void With_the_setting_on_the_SAME_device_coming_back_resumes_alone_but_a_different_one_never_does()
    {
        _settings = _settings with { ChurchDeviceId = Usb.Id, ResumeOnDeviceReturn = true, TrackFadeInMs = 250 };
        Play();
        _backend.Playbacks[0].PositionMs = 4_000;
        _devices.Disconnect(Usb.Id);
        _devices.Connect(Aux);   // otro dispositivo aparece: NO es el mismo id
        Thread.Sleep(100);
        Assert.Equal(AudioTrackPhase.DeviceLost, _engine.Snapshot()!.Phase);
        Assert.Single(_backend.Opened);

        _devices.Connect(Usb);   // mismo id
        WaitFor(() => _engine.Snapshot()!.Phase == AudioTrackPhase.Playing);
        Assert.Equal(Usb.Id, _backend.Opened[1].DeviceId);
        Assert.Contains("seek:4000", _backend.Playbacks[1].Calls);
        Assert.Contains("play:250", _backend.Playbacks[1].Calls);
    }

    [Fact]
    public void In_default_mode_a_change_of_the_windows_default_does_not_move_a_track_that_is_already_playing()
    {
        Play();   // abierta en el predeterminado de entonces (Laptop)
        _devices.SetDefault(Usb.Id);   // Windows cambió el predeterminado al conectar el USB
        Assert.Equal(AudioTrackPhase.Playing, _engine.Snapshot()!.Phase);
        Assert.Single(_backend.Opened);
        Assert.False(_backend.Playbacks[0].Disposed);
    }

    [Fact]
    public void A_playback_that_reports_the_device_was_lost_is_treated_the_same_way()
    {
        _settings = _settings with { ChurchDeviceId = Usb.Id };
        Play();
        _backend.Playbacks[0].PositionMs = 700;
        _backend.Playbacks[0].Raise(PlaybackEndReason.DeviceLost);
        Assert.Equal((AudioTrackPhase.DeviceLost, 700L), (_engine.Snapshot()!.Phase, _engine.Snapshot()!.PositionMs));
    }

    // ───────────── estado, volumen y fundidos ─────────────

    [Fact]
    public void A_track_can_be_loaded_READY_in_pause_and_only_starts_when_the_operator_resumes()
    {
        Play(start: false);
        Assert.Equal(AudioTrackPhase.Paused, _engine.Snapshot()!.Phase);
        Assert.DoesNotContain(_backend.Playbacks[0].Calls, c => c.StartsWith("play", StringComparison.Ordinal));
        _engine.Resume(2, 500);
        Assert.Contains("play:500", _backend.Playbacks[0].Calls);
        Assert.Equal(AudioTrackPhase.Playing, _engine.Snapshot()!.Phase);
    }

    [Fact]
    public void The_gain_is_the_clip_volume_times_the_master_and_both_changes_apply_to_the_track_that_is_playing()
    {
        _settings = _settings with { MasterVolumePct = 50 };
        Play(clip: 80);
        Assert.Equal(0.4f, _backend.Opened[0].Gain, 3);

        _settings = _settings with { MasterVolumePct = 100 };
        _engine.ApplyMaster();
        Assert.Equal(0.8f, _backend.Playbacks[0].Gain, 3);

        _engine.SetClipVolume(25);
        Assert.Equal(0.25f, _backend.Playbacks[0].Gain, 3);
        Assert.Equal(25, _engine.Snapshot()!.VolumePct);
    }

    [Fact]
    public void Fades_are_passed_to_the_playback_for_play_pause_resume_and_stop()
    {
        Play(fadeIn: 700);
        var pb = _backend.Playbacks[0];
        _engine.Pause(2, 400);
        _engine.Resume(3, 650);
        _engine.Stop(4, 900);
        Assert.Equal(["play:700", "pause:400", "play:650", "stop:900"], pb.Calls.Where(c => !c.StartsWith("gain", StringComparison.Ordinal) && !c.StartsWith("seek", StringComparison.Ordinal)));
        Assert.Null(_engine.Snapshot());   // detenida: sin pista
    }

    [Fact]
    public void Starting_another_track_replaces_the_first_with_a_short_fade()
    {
        Play();
        _engine.Play(8, @"C:\datos\media\files\cd\cde.mp3", 1000, false, true, 100, 500, 2);
        Assert.Contains(_backend.Playbacks[0].Calls, c => c.StartsWith("stop:", StringComparison.Ordinal));
        Assert.Equal(8, _engine.Snapshot()!.MediaId);
    }

    [Fact]
    public void The_end_of_the_track_marks_it_ended_and_resuming_starts_it_again_from_zero()
    {
        Play();
        _backend.Playbacks[0].Raise(PlaybackEndReason.Finished);
        Assert.Equal(AudioTrackPhase.Ended, _engine.Snapshot()!.Phase);
        _engine.Resume(2, 100);
        Assert.Equal(2, _backend.Opened.Count);
        Assert.DoesNotContain(_backend.Playbacks[1].Calls, c => c.StartsWith("seek", StringComparison.Ordinal));
        Assert.Equal(AudioTrackPhase.Playing, _engine.Snapshot()!.Phase);
    }

    [Fact]
    public void A_playback_failure_leaves_an_error_state_with_a_message_that_never_cites_names_or_paths()
    {
        Play();
        _backend.Playbacks[0].Raise(PlaybackEndReason.Failed, "COMException 0x8007007E");
        var snap = _engine.Snapshot()!;
        Assert.Equal((AudioTrackPhase.Error, AudioErrorCodes.PlayFailed), (snap.Phase, snap.ErrorCode));
        Assert.DoesNotContain("abc.mp3", snap.ErrorMessage ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public void A_file_that_cannot_be_opened_fails_with_a_clear_code_and_leaves_no_track()
    {
        _backend.FailOpen = _ => new InvalidOperationException("boom");
        var ex = Assert.Throws<AudioException>(() => Play());
        Assert.Equal(AudioErrorCodes.PlayFailed, ex.Code);
        Assert.Null(_engine.Snapshot());
    }

    [Fact]
    public void Nothing_to_resume_is_a_clear_error()
    {
        Assert.Equal(AudioErrorCodes.NothingPlaying, Assert.Throws<AudioException>(() => _engine.Resume(1, 100)).Code);
    }

    // ───────────── «Silencio» y el orden de llegada ─────────────

    [Fact]
    public void Silence_pauses_the_track_in_100_ms_keeps_it_loaded_and_never_resumes()
    {
        Play();
        _engine.StopAllNow(10);
        Assert.Contains("pause:100", _backend.Playbacks[0].Calls);
        Assert.Equal(AudioTrackPhase.Paused, _engine.Snapshot()!.Phase);
        _engine.StopAllNow(11);   // otro pulso: no es un interruptor
        Assert.Equal(AudioTrackPhase.Paused, _engine.Snapshot()!.Phase);
        Assert.Single(_backend.Opened);
    }

    [Fact]
    public void A_resume_numbered_BEFORE_silence_is_ignored_and_one_after_it_works()
    {
        Play();
        _engine.StopAllNow(10);
        _engine.Resume(9, 100);   // pedida antes del Silencio (llegó tarde por otro carril)
        Assert.Equal(AudioTrackPhase.Paused, _engine.Snapshot()!.Phase);
        _engine.Resume(12, 100);
        Assert.Equal(AudioTrackPhase.Playing, _engine.Snapshot()!.Phase);
    }

    [Fact]
    public async Task Silence_does_not_wait_for_a_device_that_is_slow_to_open_and_the_late_track_never_sounds()
    {
        _backend.OpenGate = new ManualResetEventSlim(false);
        var playing = Task.Run(() => Play(order: 5));   // «Reproducir» (número 5): abrir el dispositivo se cuelga
        Assert.True(_backend.OpenEntered.Wait(TimeSpan.FromSeconds(5)));

        var started = DateTime.UtcNow;
        var silence = Task.Run(() => _engine.StopAllNow(6));   // el atajo global (número 6)
        await silence.WaitAsync(TimeSpan.FromSeconds(2));   // si el Silencio esperara a la apertura colgada, esto lanzaría TimeoutException
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));

        _backend.OpenGate.Set();   // por fin el dispositivo abre
        await playing.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(_engine.Snapshot());                       // la pista pedida antes del Silencio NO se instaló
        WaitFor(() => _backend.Playbacks[0].Disposed);
        Assert.DoesNotContain(_backend.Playbacks[0].Calls, c => c.StartsWith("play", StringComparison.Ordinal));   // nunca sonó
    }

    [Fact]
    public void The_gain_state_is_visible_to_the_panel_through_the_snapshot_fields()
    {
        Play(loop: true, clip: 60);
        var snap = _engine.Snapshot()!;
        Assert.Equal((7L, true, 60, 60_000L), (snap.MediaId, snap.Loop, snap.VolumePct, snap.DurationMs));
        Assert.True(_backend.Opened[0].Loop);
    }
}

/// <summary>Reglas puras del audio: ruta (predeterminado de Windows contra dispositivo de la iglesia) y validación de los ajustes.</summary>
public class AudioRulesTests
{
    private static readonly AudioDeviceInfo Laptop = new("id-laptop", "Altavoces del portátil");
    private static readonly AudioDeviceInfo Usb = new("id-usb", "Interfaz USB");

    [Fact]
    public void No_church_device_means_the_tracks_use_the_windows_default_and_there_is_no_mismatch()
    {
        var r = AudioRoutePolicy.Evaluate(new AudioSettings(), [Laptop, Usb], Laptop);
        Assert.Equal((true, true, "id-laptop", false), (r.UsesWindowsDefault, r.ChurchConnected, r.TracksDeviceId, r.Mismatch));
    }

    [Fact]
    public void Church_device_that_is_also_the_windows_default_has_no_mismatch()
    {
        var r = AudioRoutePolicy.Evaluate(new AudioSettings(ChurchDeviceId: "id-usb"), [Laptop, Usb], Usb);
        Assert.Equal((false, false, "id-usb"), (r.UsesWindowsDefault, r.Mismatch, r.TracksDeviceId));
    }

    [Fact]
    public void Church_device_connected_but_not_the_windows_default_is_a_visible_mismatch()
    {
        // Windows cambió el predeterminado al conectar o quitar el USB: las pistas irían por la mezcladora y el video por los altavoces del portátil.
        var r = AudioRoutePolicy.Evaluate(new AudioSettings(ChurchDeviceId: "id-usb"), [Laptop, Usb], Laptop);
        Assert.True(r.Mismatch);
        Assert.Equal(("id-usb", "id-laptop"), (r.TracksDeviceId, r.DefaultDeviceId));
    }

    [Fact]
    public void A_missing_church_device_is_reported_not_replaced_and_keeps_its_last_known_name()
    {
        var r = AudioRoutePolicy.Evaluate(new AudioSettings(ChurchDeviceId: "id-usb", ChurchDeviceName: "Interfaz USB"), [Laptop], Laptop);
        Assert.Equal((false, null, "Interfaz USB", false), (r.ChurchConnected, r.TracksDeviceId, r.ChurchDeviceName, r.Mismatch));
    }

    [Fact]
    public void Devices_are_matched_by_id_exactly_not_by_name()
    {
        var sameNameOtherId = new AudioDeviceInfo("id-otro", "Interfaz USB");
        var r = AudioRoutePolicy.Evaluate(new AudioSettings(ChurchDeviceId: "id-usb", ChurchDeviceName: "Interfaz USB"), [sameNameOtherId], sameNameOtherId);
        Assert.False(r.ChurchConnected);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-1)]
    public void The_master_volume_must_be_between_0_and_100(int volume) =>
        Assert.NotNull(AudioRules.Validate(new AudioSettings(MasterVolumePct: volume)));

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 5001)]
    public void Fades_must_be_between_0_and_5000_ms(int fadeIn, int fadeOut) =>
        Assert.NotNull(AudioRules.Validate(new AudioSettings(TrackFadeInMs: fadeIn, TrackFadeOutMs: fadeOut)));

    [Fact]
    public void Defaults_are_valid_start_at_half_volume_and_do_not_resume_alone()
    {
        var d = new AudioSettings();
        Assert.Null(AudioRules.Validate(d));
        Assert.Equal(50, d.MasterVolumePct);
        Assert.False(d.ResumeOnDeviceReturn);
        Assert.Null(d.ChurchDeviceId);
    }

    [Fact]
    public void A_device_id_with_control_characters_or_empty_is_rejected()
    {
        Assert.NotNull(AudioRules.Validate(new AudioSettings(ChurchDeviceId: "")));
        Assert.NotNull(AudioRules.Validate(new AudioSettings(ChurchDeviceId: "id\u0001")));
        Assert.NotNull(AudioRules.Validate(new AudioSettings(ChurchDeviceId: new string('x', 401))));
    }
}
