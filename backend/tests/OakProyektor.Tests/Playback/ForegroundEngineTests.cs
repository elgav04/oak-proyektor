using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Playback;

/// <summary>Transiciones del video en primer plano (<see cref="ForegroundEngine"/> y las reglas de <see cref="OutputState"/>): lógica pura, sin hilos ni ventanas.</summary>
public class ForegroundEngineTests
{
    private readonly ForegroundEngine _engine = new();
    private OutputState _state = OutputState.Initial.WithMaster(MasterMode.Live);

    private static ForegroundRequest Request(bool start = true, int clip = 100, int master = 50, ForegroundEndAction onEnd = ForegroundEndAction.Back, long mediaId = 7) =>
        new(mediaId, "file:///C:/datos/media/files/ab/abc.mp4", "Video de prueba", start, clip, master, false, false, 400, 400, ForegroundBackdrop.Black, onEnd);

    private ForegroundOutcome Play(ForegroundRequest? r = null, long order = 1, bool hidden = false)
    {
        var o = _engine.Play(_state, r ?? Request(), order, hidden);
        if (o.Applied) _state = o.State;
        return o;
    }

    private ForegroundOutcome Apply(Func<OutputState, ForegroundOutcome> step)
    {
        var o = step(_state);
        if (o.Applied) _state = o.State;
        return o;
    }

    [Fact]
    public void Play_puts_the_video_in_the_foreground_and_leaves_background_content_and_overlays_alone()
    {
        var withLayers = _state
            .WithBackground(new BackgroundLayer(BackgroundKind.Video, "#112233", "file:///C:/v/fondo.mp4", "fondo.mp4", true, 600, null))
            .WithSlide(new Slide("s1", [new TextBlock("texto")], TextTheme.Default), 400);
        _state = withLayers;
        Play();
        Assert.NotNull(_state.Foreground);
        Assert.Equal(withLayers.Background, _state.Background);   // el fondo en bucle de debajo no se toca (mismo medio, sin reintento)
        Assert.Equal(withLayers.Content, _state.Content);
        Assert.Equal(withLayers.Overlays, _state.Overlays);
        Assert.Equal(MasterMode.Live, _state.Master);
    }

    [Fact]
    public void A_video_starts_playing_when_visible_and_loads_PAUSED_when_the_output_would_not_show_it()
    {
        Assert.Equal(ForegroundPhase.Playing, Play(hidden: false).State.Foreground!.Phase);
        Assert.Equal(ForegroundPhase.Paused, Play(hidden: true).State.Foreground!.Phase);   // nunca suena oculto
        Assert.Equal(ForegroundPhase.Paused, Play(Request(start: false)).State.Foreground!.Phase);
    }

    [Fact]
    public void The_effective_volume_is_the_clip_volume_times_the_master_volume_and_every_play_is_a_new_element()
    {
        var first = Play(Request(clip: 80, master: 50)).State.Foreground!;
        Assert.Equal((80, 40), (first.ClipVolumePct, first.VolumePct));
        var second = Play(Request(clip: 80, master: 50), order: 2).State.Foreground!;
        Assert.NotEqual(first.Id, second.Id);   // la salida reinicia el elemento en cada reproducción nueva
        Assert.Equal(40, ForegroundMedia.Effective(100, 40));
        Assert.Equal(0, ForegroundMedia.Effective(100, 0));
        Assert.Equal(100, ForegroundMedia.Effective(150, 150));   // acotado
    }

    [Fact]
    public void Pause_and_resume_keep_the_same_element_and_ended_restarts_from_the_beginning()
    {
        var id = Play().State.Foreground!.Id;
        Apply(s => _engine.Pause(s, 2));
        Assert.Equal((id, ForegroundPhase.Paused), (_state.Foreground!.Id, _state.Foreground.Phase));
        Apply(s => _engine.Resume(s, 3));
        Assert.Equal((id, ForegroundPhase.Playing, 0), (_state.Foreground!.Id, _state.Foreground.Phase, _state.Foreground.SeekSeq));

        // «Quedarse» al terminar: queda en su último fotograma; reanudar lo reinicia (sube SeekSeq para que la salida lo vuelva a 0 aunque el navegador lo deje al final).
        _state = ForegroundEngine.Ended(Play(Request(onEnd: ForegroundEndAction.Stay), order: 4).State, _state.Foreground!.Id);
        Assert.Equal(ForegroundPhase.Ended, _state.Foreground!.Phase);
        Apply(s => _engine.Resume(s, 5));
        Assert.Equal((ForegroundPhase.Playing, 0L, 1), (_state.Foreground!.Phase, _state.Foreground.SeekMs, _state.Foreground.SeekSeq));
    }

    [Fact]
    public void Seek_changes_the_position_request_every_time_even_to_the_same_place()
    {
        Play();
        _state = _engine.Seek(_state, 5000);
        _state = _engine.Seek(_state, 5000);
        Assert.Equal((5000L, 2), (_state.Foreground!.SeekMs, _state.Foreground.SeekSeq));
        Assert.Equal(0, _engine.Seek(_state, -10).Foreground!.SeekMs);
    }

    [Fact]
    public void Volume_and_mute_change_only_what_is_asked_and_the_master_recomputes_the_effective_volume()
    {
        Play(Request(clip: 100, master: 50));
        _state = ForegroundEngine.SetVolume(_state, 60, null, 50);
        Assert.Equal((60, 30, false), (_state.Foreground!.ClipVolumePct, _state.Foreground.VolumePct, _state.Foreground.Muted));
        _state = ForegroundEngine.SetVolume(_state, null, true, 50);
        Assert.Equal((60, 30, true), (_state.Foreground!.ClipVolumePct, _state.Foreground.VolumePct, _state.Foreground.Muted));
        _state = ForegroundEngine.ApplyMaster(_state, 100);
        Assert.Equal(60, _state.Foreground!.VolumePct);
    }

    [Theory]
    [InlineData(MasterMode.ClearText)]
    [InlineData(MasterMode.Black)]
    [InlineData(MasterMode.Logo)]
    public void Clear_text_black_and_logo_PAUSE_the_video_and_going_back_to_live_does_not_resume_it(MasterMode hiding)
    {
        Play();
        _state = _state.WithMaster(hiding);
        Assert.Equal(ForegroundPhase.Paused, _state.Foreground!.Phase);
        _state = _state.WithMaster(MasterMode.Live);
        Assert.Equal(ForegroundPhase.Paused, _state.Foreground!.Phase);   // reanudar es una acción del operador
    }

    [Fact]
    public void Freeze_does_not_freeze_or_pause_the_foreground()
    {
        var id = Play().State.Foreground!.Id;
        var frozen = _state.WithMaster(MasterMode.Freeze);
        Assert.Equal((id, ForegroundPhase.Playing), (frozen.Foreground!.Id, frozen.Foreground.Phase));
        // Y se puede pausar/detener con Freeze activo: el estado sigue aceptando las órdenes.
        _state = frozen;
        Apply(s => _engine.Pause(s, 9));
        Assert.Equal(ForegroundPhase.Paused, _state.Foreground!.Phase);
    }

    [Fact]
    public void The_end_of_the_video_either_removes_it_or_stays_on_the_last_frame_and_a_stale_report_is_ignored()
    {
        var id = Play(Request(onEnd: ForegroundEndAction.Back)).State.Foreground!.Id;
        Assert.Equal(_state, ForegroundEngine.Ended(_state, "otro-video"));   // un informe de un video que ya no es el actual
        Assert.Null(ForegroundEngine.Ended(_state, id).Foreground);

        var id2 = Play(Request(onEnd: ForegroundEndAction.Stay), order: 2).State.Foreground!.Id;
        var stayed = ForegroundEngine.Ended(_state, id2);
        Assert.Equal((id2, ForegroundPhase.Ended), (stayed.Foreground!.Id, stayed.Foreground.Phase));
    }

    [Fact]
    public void Stop_removes_the_video_and_only_the_video()
    {
        _state = _state.WithSlide(new Slide("s1", [new TextBlock("x")], TextTheme.Default), 400);
        Play();
        Apply(s => _engine.Stop(s, 5));
        Assert.Null(_state.Foreground);
        Assert.NotNull(_state.Content.Slide);
    }

    // ───────────── «Silencio» y el orden de llegada ─────────────

    [Fact]
    public void Silence_only_pauses_it_never_resumes_never_removes_and_a_second_press_is_not_a_toggle()
    {
        var id = Play().State.Foreground!.Id;
        _state = _engine.StopAll(_state, 10);
        Assert.Equal((id, ForegroundPhase.Paused), (_state.Foreground!.Id, _state.Foreground.Phase));
        _state = _engine.StopAll(_state, 11);
        Assert.Equal(ForegroundPhase.Paused, _state.Foreground!.Phase);   // otro pulso no lo reanuda
        var empty = _state with { Foreground = null };
        Assert.Equal(empty, _engine.StopAll(empty, 12));   // sin video no inventa nada
    }

    [Fact]
    public void A_play_that_was_queued_BEFORE_silence_cannot_make_the_video_sound_again()
    {
        // El operador pulsa «Reproducir» (número 5, va por el carril lento y tarda) y luego «Silencio» (número 6, prioridad: llega antes).
        _state = _engine.StopAll(_state, 6);
        var late = _engine.Play(_state, Request(), order: 5, hidden: false);
        Assert.False(late.Applied);
        Assert.Null(_state.Foreground);
        // Un «reproducir» pedido DESPUÉS sí funciona (el operador cambió de idea).
        Assert.True(_engine.Play(_state, Request(), order: 7, hidden: false).Applied);
    }

    [Fact]
    public void The_last_intention_wins_even_when_pause_and_resume_arrive_out_of_order()
    {
        Play(order: 1);
        // El operador pulsa pausa (10) y reanudar (11); reanudar llega primero por otro carril y pausa después.
        Assert.True(Apply(s => _engine.Resume(s, 11)).Applied);
        Assert.False(Apply(s => _engine.Pause(s, 10)).Applied);   // ya vencida por una orden posterior
        Assert.Equal(ForegroundPhase.Playing, _state.Foreground!.Phase);

        // Al revés: pausa (21) llega antes que reanudar (20): la reanudación vencida no arranca el video.
        Assert.True(Apply(s => _engine.Pause(s, 21)).Applied);
        Assert.False(Apply(s => _engine.Resume(s, 20)).Applied);
        Assert.Equal(ForegroundPhase.Paused, _state.Foreground!.Phase);
    }

    [Fact]
    public void Loading_a_video_READY_without_playing_is_not_blocked_by_an_earlier_silence()
    {
        _engine.StopAll(_state, 50);
        Assert.True(_engine.Play(_state, Request(start: false), order: 3, hidden: false).Applied);   // cargar listo no suena
    }

    [Fact]
    public void A_stale_stop_cannot_remove_a_video_that_the_operator_started_later()
    {
        Play(order: 1);
        Assert.True(Apply(s => _engine.Resume(s, 30)).Applied);
        Assert.False(Apply(s => _engine.Stop(s, 29)).Applied);
        Assert.NotNull(_state.Foreground);
    }
}
