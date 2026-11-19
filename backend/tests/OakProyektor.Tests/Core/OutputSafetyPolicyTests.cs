using OakProyektor.Core.Display;
using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Core;

public class OutputSafetyPolicyTests
{
    private static readonly DisplayInfo Panel = new(DisplayId.From(0, 0, 1920, 1080), 0, 0, 1920, 1080, 1.25, true, true);
    private static readonly DisplayInfo Projector = new(DisplayId.From(1920, 0, 1920, 1080), 1920, 0, 1920, 1080, 1, false, false);
    private static readonly ScreenRect ProjectorRect = ScreenRect.Of(Projector);

    private static OutputSafetyDecision Eval(
        ScreenRect? output = null, bool topmost = true, IReadOnlyList<DisplayInfo>? displays = null,
        bool paused = false, OutputPauseReason? reason = null, bool autoRestore = true) =>
        OutputSafetyPolicy.Evaluate(new OutputSafetyInput(output ?? ProjectorRect, topmost, displays ?? [Panel, Projector], Projector.Id, paused, reason, autoRestore));

    [Fact]
    public void Healthy_output_fully_inside_the_projector_needs_nothing()
    {
        Assert.Equal(SafetyActionKind.None, Eval().Kind);
    }

    [Fact]
    public void Projector_disconnected_pauses_with_display_lost()
    {
        var d = Eval(displays: [Panel]);
        Assert.Equal((SafetyActionKind.Pause, OutputPauseReason.DisplayLost), (d.Kind, d.Reason));
    }

    [Fact]
    public void A_missing_projector_with_the_output_still_away_from_the_panel_waits_for_the_debounce()
    {
        Assert.False(Eval(displays: [Panel]).Immediate);   // un parpadeo de enumeración: el controlador espera 2 ciclos
    }

    [Fact]
    public void A_missing_projector_while_the_output_already_covers_the_panel_pauses_immediately_without_debounce()
    {
        // Windows movió la ventana al monitor que queda (el del panel): tapa al operador y no se puede esperar ~1 s.
        var d = Eval(output: new ScreenRect(0, 0, 1920, 1080), displays: [Panel]);
        Assert.Equal((SafetyActionKind.Pause, OutputPauseReason.DisplayLost, true), (d.Kind, d.Reason, d.Immediate));
    }

    [Fact]
    public void Even_a_partial_overlap_with_the_panel_counts_as_covering_it()
    {
        var d = Eval(output: new ScreenRect(1800, 0, 1920, 1080), displays: [Panel]);   // 120 px sobre el panel
        Assert.True(d.Immediate);
    }

    [Fact]
    public void Windows_moving_the_output_onto_the_panel_monitor_pauses_even_if_the_projector_still_lists()
    {
        var d = Eval(output: new ScreenRect(0, 0, 1920, 1080));
        Assert.Equal((SafetyActionKind.Pause, OutputPauseReason.PanelCrossed), (d.Kind, d.Reason));
    }

    [Fact]
    public void Partially_overlapping_the_panel_monitor_pauses()
    {
        var d = Eval(output: new ScreenRect(1900, 0, 1920, 1080));   // 20 px sobre el panel
        Assert.Equal((SafetyActionKind.Pause, OutputPauseReason.PanelCrossed), (d.Kind, d.Reason));
    }

    [Fact]
    public void Panel_dragged_onto_the_output_monitor_pauses()
    {
        var panelOnProjector = Panel with { HostsPanel = false };
        var projectorWithPanel = Projector with { HostsPanel = true };
        var d = Eval(displays: [panelOnProjector, projectorWithPanel]);
        Assert.Equal((SafetyActionKind.Pause, OutputPauseReason.PanelCrossed), (d.Kind, d.Reason));
    }

    [Fact]
    public void Not_fully_inside_the_projector_but_not_touching_the_panel_only_drops_topmost()
    {
        var d = Eval(output: new ScreenRect(1920, 0, 1920, 1200), topmost: true);   // se sale por abajo, sin tocar el panel
        Assert.Equal((SafetyActionKind.SetTopmost, false), (d.Kind, d.Topmost));
    }

    [Fact]
    public void Topmost_is_restored_once_fully_inside_again()
    {
        var d = Eval(topmost: false);
        Assert.Equal((SafetyActionKind.SetTopmost, true), (d.Kind, d.Topmost));
    }

    [Theory]
    [InlineData(OutputPauseReason.DisplayLost)]
    [InlineData(OutputPauseReason.PanelCrossed)]
    public void Automatic_pauses_resume_when_the_same_monitor_is_back_and_free_of_the_panel(OutputPauseReason reason)
    {
        Assert.Equal(SafetyActionKind.Resume, Eval(paused: true, reason: reason, output: new ScreenRect(-32000, -32000, 160, 28)).Kind);
    }

    [Fact]
    public void Auto_restore_disabled_never_resumes()
    {
        Assert.Equal(SafetyActionKind.None, Eval(paused: true, reason: OutputPauseReason.DisplayLost, autoRestore: false).Kind);
    }

    [Theory]
    [InlineData(OutputPauseReason.Manual)]
    [InlineData(OutputPauseReason.Emergency)]
    public void Manual_and_emergency_pauses_wait_for_the_operator(OutputPauseReason reason)
    {
        Assert.Equal(SafetyActionKind.None, Eval(paused: true, reason: reason).Kind);
    }

    [Fact]
    public void Stays_paused_while_the_monitor_is_still_missing_or_the_panel_is_still_on_it()
    {
        Assert.Equal(SafetyActionKind.None, Eval(paused: true, reason: OutputPauseReason.DisplayLost, displays: [Panel]).Kind);
        Assert.Equal(SafetyActionKind.None, Eval(paused: true, reason: OutputPauseReason.PanelCrossed, displays: [Panel with { HostsPanel = false }, Projector with { HostsPanel = true }]).Kind);
    }

    [Fact]
    public void A_different_monitor_at_another_position_is_not_the_same_monitor()
    {
        var other = new DisplayInfo(DisplayId.From(1920, 0, 1280, 720), 1920, 0, 1280, 720, 1, false, false);   // otro proyector con otra resolución
        Assert.Equal(SafetyActionKind.None, Eval(paused: true, reason: OutputPauseReason.DisplayLost, displays: [Panel, other]).Kind);
    }

    [Fact]
    public void An_empty_enumeration_is_treated_as_unreliable_not_as_a_disconnection()
    {
        Assert.Equal(SafetyActionKind.None, Eval(displays: []).Kind);
    }

    [Fact]
    public void The_monitor_is_found_by_id_even_if_the_list_order_changes()
    {
        // Antes (por índice) esto apuntaba al monitor equivocado: el proyector pasa de la posición 1 a la 0.
        var d = Eval(displays: [Projector, Panel]);
        Assert.Equal(SafetyActionKind.None, d.Kind);
    }

    [Fact]
    public void Rect_geometry()
    {
        var a = new ScreenRect(0, 0, 100, 100);
        Assert.False(a.Intersects(new ScreenRect(100, 0, 50, 50)));   // se tocan en el borde: no se cruzan
        Assert.True(a.Intersects(new ScreenRect(99, 99, 50, 50)));
        Assert.True(new ScreenRect(10, 10, 20, 20).IsInside(a));
        Assert.False(new ScreenRect(90, 10, 20, 20).IsInside(a));
    }
}
