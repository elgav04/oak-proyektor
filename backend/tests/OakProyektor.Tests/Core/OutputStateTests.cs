using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Core;

public class OutputStateTests
{
    private static Slide SlideOf(string id, string text = "texto") => new(id, [new TextBlock(text)], TextTheme.Default);
    private static BackgroundLayer Video(string url = "file:///C:/v/a.mp4") => new(BackgroundKind.Video, "#112233", url, "a.mp4", true, 600, null);

    [Fact]
    public void Starts_black_without_content_background_or_overlays_at_revision_zero_and_schema_one()
    {
        var s = OutputState.Initial;
        Assert.Equal((OutputState.CurrentSchemaVersion, 0L, MasterMode.Black), (s.SchemaVersion, s.Revision, s.Master));
        Assert.Equal(1, s.SchemaVersion);
        Assert.Equal(BackgroundKind.None, s.Background.Kind);
        Assert.Null(s.Content.Slide);
        Assert.Empty(s.Overlays.Items);
    }

    [Fact]
    public void Master_modes_only_change_the_mode_and_keep_every_layer()
    {
        var loaded = OutputState.Initial.WithBackground(Video()).WithSlide(SlideOf("s1"), 400);
        foreach (var mode in Enum.GetValues<MasterMode>())
        {
            var s = loaded.WithMaster(mode);
            Assert.Equal(mode, s.Master);
            Assert.Equal(loaded.Background, s.Background);
            Assert.Same(loaded.Content, s.Content);
            Assert.Same(loaded.Overlays, s.Overlays);
        }
    }

    [Fact]
    public void Repeating_the_same_master_changes_nothing()
    {
        var live = OutputState.Initial.WithMaster(MasterMode.Live);
        Assert.Same(live, live.WithMaster(MasterMode.Live));
    }

    [Fact]
    public void Revision_only_grows_and_only_with_master_changes()
    {
        var s = OutputState.Initial;
        long last = s.Revision;
        foreach (var mode in new[] { MasterMode.Live, MasterMode.Logo, MasterMode.Freeze, MasterMode.Black, MasterMode.ClearText })
        {
            s = s.WithMaster(mode);
            Assert.True(s.Revision > last);
            last = s.Revision;
        }
        var after = s.WithBackground(Video()).WithSlide(SlideOf("s1"), 400).WithoutSlide(400);
        Assert.Equal(last, after.Revision);
    }

    [Fact]
    public void Unknown_master_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputState.Initial.WithMaster((MasterMode)99));
    }

    [Fact]
    public void Changing_the_content_never_touches_the_background_and_changing_the_background_never_touches_the_content()
    {
        // La garantía de «el video no se reinicia al cambiar el texto» empieza aquí: el fondo es el MISMO objeto.
        var s = OutputState.Initial.WithMaster(MasterMode.Live).WithBackground(Video());
        var bg = s.Background;
        for (var i = 0; i < 50; i++) s = s.WithSlide(SlideOf($"s{i}", $"texto {i}"), 400);
        Assert.Same(bg, s.Background);

        var slide = s.Content.Slide;
        s = s.WithBackground(BackgroundLayer.OfColor("#ff0000"));
        Assert.Same(slide, s.Content.Slide);
    }

    [Fact]
    public void The_same_color_or_none_background_is_not_a_change()
    {
        var color = OutputState.Initial.WithBackground(BackgroundLayer.OfColor("#102030"));
        Assert.Same(color, color.WithBackground(BackgroundLayer.OfColor("#102030")));   // iguales por valor: no se publica nada
        Assert.Same(color, color.WithBackgroundRetry());                                // sin medio no hay nada que reintentar
        Assert.Same(OutputState.Initial, OutputState.Initial.WithBackground(BackgroundLayer.Empty));
        var s = OutputState.Initial.WithBackground(Video());
        Assert.NotSame(s, s.WithBackground(Video("file:///C:/v/b.mp4")));
    }

    [Fact]
    public void Reapplying_the_same_media_file_counts_as_a_retry_so_the_state_changes_and_the_output_can_try_again()
    {
        var s = OutputState.Initial.WithBackground(Video());
        Assert.Equal(0, s.Background.Retry);
        var again = s.WithBackground(Video());
        Assert.NotEqual(s, again);                       // NO se descarta: la salida debe enterarse
        Assert.Equal(1, again.Background.Retry);
        Assert.Equal(2, again.WithBackground(Video()).Background.Retry);
        Assert.Equal(3, again.WithBackground(Video()).WithBackgroundRetry().Background.Retry);
        // Solo cambia el contador: el resto del fondo es el mismo.
        Assert.Equal(s.Background, again.Background with { Retry = 0 });
    }

    [Fact]
    public void Changing_the_media_file_resets_the_retry_counter()
    {
        var s = OutputState.Initial.WithBackground(Video()).WithBackgroundRetry().WithBackgroundRetry();
        var other = s.WithBackground(Video("file:///C:/v/b.mp4"));
        Assert.Equal(0, other.Background.Retry);
        Assert.Equal(0, other.WithBackground(Video("file:///C:/v/a.mp4")).Background.Retry);
    }

    [Fact]
    public void A_retry_never_touches_the_content_or_the_master_mode()
    {
        var s = OutputState.Initial.WithMaster(MasterMode.Live).WithBackground(Video()).WithSlide(SlideOf("s1"), 400);
        var retried = s.WithBackgroundRetry();
        Assert.Same(s.Content, retried.Content);
        Assert.Equal(s.Master, retried.Master);
        Assert.Equal(s.Revision, retried.Revision);
    }

    [Fact]
    public void A_new_slide_replaces_the_old_one_and_removing_it_is_not_the_clear_master()
    {
        var s = OutputState.Initial.WithSlide(SlideOf("s1"), 300);
        Assert.Equal("s1", s.Content.Slide?.Id);
        Assert.Equal(300, s.Content.TransitionMs);

        s = s.WithSlide(SlideOf("s2"), 500);
        Assert.Equal("s2", s.Content.Slide?.Id);

        // Clear (maestro) OCULTA: el slide sigue en el estado y vuelve con Live. clearContent lo BORRA.
        var cleared = s.WithMaster(MasterMode.ClearText);
        Assert.Equal("s2", cleared.Content.Slide?.Id);
        Assert.Null(s.WithoutSlide(200).Content.Slide);
        Assert.Null(s.WithoutSlide(200).WithoutSlide(200).Content.Slide);   // sin slide, quitarlo de nuevo no cambia nada
    }

    [Fact]
    public void Freeze_is_just_a_mode_in_the_state_which_keeps_accumulating_changes_for_the_output_to_hold_back()
    {
        // La salida es quien «no aplica» snapshots durante Freeze (frontend/src/output/freeze.ts); el estado sigue siendo la verdad y acumula.
        var s = OutputState.Initial.WithMaster(MasterMode.Live).WithSlide(SlideOf("s1"), 400).WithMaster(MasterMode.Freeze).WithSlide(SlideOf("s2"), 400);
        Assert.Equal(MasterMode.Freeze, s.Master);
        Assert.Equal("s2", s.Content.Slide?.Id);
    }

    [Fact]
    public void Every_layer_has_a_value_object_shape_for_the_wire()
    {
        // OutputState se serializa tal cual: todo debe ser un record con valores (sin referencias a servicios ni estados mutables).
        var json = System.Text.Json.JsonSerializer.Serialize(OutputState.Initial.WithBackground(Video()).WithSlide(SlideOf("s1"), 400), OakProyektor.Ipc.IpcSerializer.Options);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var names = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["schemaVersion", "revision", "master", "background", "content", "overlays"], names);
    }
}
