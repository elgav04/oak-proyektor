using OakProyektor.Core.Display;
using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Core;

/// <summary>Identidad estable de monitor y decisión de reajuste (funciones puras). Hallazgos de las pruebas con el proyector real.</summary>
public class MonitorIdentityAndRefitTests
{
    private const string PanelPath = @"\\?\DISPLAY#AUOEB83#4&1331027&2&UID8388688#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
    private const string ProjectorPath = @"\\?\DISPLAY#VTK2150#4&1331027&2&UID4145#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static readonly ScreenRect PanelRect = new(0, 0, 1920, 1080);
    private static readonly ScreenRect ProjectorRect1080 = new(1920, 0, 1920, 1080);

    private static IReadOnlyList<DisplayInfo> Build(params (DisplayCandidate C, double Scale, bool Panel)[] monitors)
    {
        var ids = DisplayId.Assign([.. monitors.Select(m => m.C)]);
        return [.. monitors.Select((m, i) => new DisplayInfo(ids[i], m.C.Rect.X, m.C.Rect.Y, m.C.Rect.Width, m.C.Rect.Height, m.Scale, m.C.Rect is { X: 0, Y: 0 }, m.Panel))];
    }

    private static string IdOf(IReadOnlyList<DisplayInfo> list, ScreenRect rect) => list.Single(d => ScreenRect.Of(d) == rect).Id;

    // ───────────── identidad ─────────────

    [Fact]
    public void Changing_the_resolution_of_the_same_monitor_keeps_its_id()
    {
        var before = Build((new(PanelRect, PanelPath), 1.25, true), (new(ProjectorRect1080, ProjectorPath), 1, false));
        var after = Build((new(PanelRect, PanelPath), 1.25, true), (new(new ScreenRect(1920, 0, 1280, 720), ProjectorPath), 1, false));
        Assert.Equal(IdOf(before, ProjectorRect1080), IdOf(after, new ScreenRect(1920, 0, 1280, 720)));
    }

    [Fact]
    public void Changing_the_scale_of_the_same_monitor_keeps_its_id()
    {
        var before = Build((new(PanelRect, PanelPath), 1, true), (new(ProjectorRect1080, ProjectorPath), 1, false));
        var after = Build((new(PanelRect, PanelPath), 1.5, true), (new(ProjectorRect1080, ProjectorPath), 1.5, false));
        Assert.Equal(before.Select(d => d.Id), after.Select(d => d.Id));
    }

    [Fact]
    public void Swapping_the_primary_monitor_keeps_each_monitors_id()
    {
        // Antes: el panel en (0,0) y el proyector a su derecha. Después: el proyector pasa a ser el principal (0,0) y el panel queda a su izquierda.
        var before = Build((new(PanelRect, PanelPath), 1.25, true), (new(ProjectorRect1080, ProjectorPath), 1, false));
        var swapped = Build((new(new ScreenRect(-1920, 0, 1920, 1080), PanelPath), 1.25, true), (new(new ScreenRect(0, 0, 1920, 1080), ProjectorPath), 1, false));
        Assert.Equal(IdOf(before, ProjectorRect1080), IdOf(swapped, new ScreenRect(0, 0, 1920, 1080)));
        Assert.Equal(IdOf(before, PanelRect), IdOf(swapped, new ScreenRect(-1920, 0, 1920, 1080)));
    }

    [Fact]
    public void Moving_a_monitor_in_the_virtual_desktop_keeps_its_id()
    {
        var a = Build((new(PanelRect, PanelPath), 1, true), (new(ProjectorRect1080, ProjectorPath), 1, false));
        var b = Build((new(PanelRect, PanelPath), 1, true), (new(new ScreenRect(0, -1080, 1920, 1080), ProjectorPath), 1, false));
        Assert.Equal(a.Select(d => d.Id).Order(), b.Select(d => d.Id).Order());
    }

    [Fact]
    public void The_id_does_not_depend_on_the_enumeration_order()
    {
        var a = Build((new(PanelRect, PanelPath), 1, true), (new(ProjectorRect1080, ProjectorPath), 1, false));
        var b = Build((new(ProjectorRect1080, ProjectorPath), 1, false), (new(PanelRect, PanelPath), 1, true));
        Assert.Equal(IdOf(a, ProjectorRect1080), IdOf(b, ProjectorRect1080));
    }

    [Fact]
    public void A_different_physical_monitor_is_a_different_id()
    {
        var other = @"\\?\DISPLAY#GSM5B09#4&1331027&2&UID9999#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";
        var a = Build((new(ProjectorRect1080, ProjectorPath), 1, false));
        var b = Build((new(ProjectorRect1080, other), 1, false));
        Assert.NotEqual(a[0].Id, b[0].Id);
    }

    [Fact]
    public void Device_ids_are_case_insensitive_and_distinguishable_from_the_rect_fallback()
    {
        Assert.Equal(DisplayId.FromDevice(ProjectorPath), DisplayId.FromDevice(ProjectorPath.ToUpperInvariant()));
        Assert.StartsWith("dev:", DisplayId.FromDevice(ProjectorPath));
        Assert.DoesNotContain("dev:", DisplayId.From(0, 0, 1, 1));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_device_path_the_id_falls_back_to_the_rectangle(string? path)
    {
        var ids = DisplayId.Assign([new DisplayCandidate(ProjectorRect1080, path)]);
        Assert.Equal(DisplayId.From(1920, 0, 1920, 1080), ids[0]);
    }

    [Fact]
    public void Identical_device_paths_still_give_unique_ids_ordered_by_position()
    {
        var ids = DisplayId.Assign([new DisplayCandidate(ProjectorRect1080, ProjectorPath), new DisplayCandidate(PanelRect, ProjectorPath)]);
        Assert.Equal(2, ids.Distinct().Count());
        Assert.Equal(DisplayId.FromDevice(ProjectorPath), ids[1]);   // el de más a la izquierda conserva el ID sin sufijo
        Assert.EndsWith("#2", ids[0]);
    }

    [Fact]
    public void A_resolution_change_is_not_a_new_monitor_for_the_resolver()
    {
        var before = Build((new(PanelRect, PanelPath), 1, true), (new(ProjectorRect1080, ProjectorPath), 1, false));
        var projectorId = IdOf(before, ProjectorRect1080);
        var after = Build((new(PanelRect, PanelPath), 1, true), (new(new ScreenRect(1920, 0, 1280, 720), ProjectorPath), 1, false));
        var r = DisplayResolver.Resolve(after, projectorId);
        Assert.Equal(DisplayResolutionKind.Chosen, r.Kind);
        Assert.Equal(1280, r.Display!.Width);
    }

    // ───────────── reajuste ─────────────

    private static OutputSafetyDecision Eval(
        IReadOnlyList<DisplayInfo> displays, string targetId, ScreenRect output, ScreenRect? applied, bool topmost = true, bool paused = false,
        OutputPauseReason? reason = null) =>
        OutputSafetyPolicy.Evaluate(new OutputSafetyInput(output, topmost, displays, targetId, paused, reason, true, applied));

    [Fact]
    public void The_same_monitor_with_a_new_resolution_refits_without_pausing()
    {
        var newRect = new ScreenRect(1920, 0, 1280, 720);
        var displays = Build((new(PanelRect, PanelPath), 1, true), (new(newRect, ProjectorPath), 1, false));
        var d = Eval(displays, IdOf(displays, newRect), output: ProjectorRect1080, applied: ProjectorRect1080);
        Assert.Equal(SafetyActionKind.Refit, d.Kind);
        Assert.Equal(newRect, d.Area);
        Assert.Null(d.Reason);
    }

    [Fact]
    public void A_scale_change_alone_needs_no_action()
    {
        // El rectángulo físico no cambia con el escalado: no hay nada que reajustar ni que pausar.
        var displays = Build((new(PanelRect, PanelPath), 1, true), (new(ProjectorRect1080, ProjectorPath), 1.75, false));
        var d = Eval(displays, IdOf(displays, ProjectorRect1080), output: ProjectorRect1080, applied: ProjectorRect1080);
        Assert.Equal(SafetyActionKind.None, d.Kind);
    }

    [Fact]
    public void Swapping_the_primary_monitor_refits_to_the_projectors_new_position()
    {
        var projectorNow = new ScreenRect(0, 0, 1920, 1080);
        var displays = Build((new(new ScreenRect(-1920, 0, 1920, 1080), PanelPath), 1.25, true), (new(projectorNow, ProjectorPath), 1, false));
        var d = Eval(displays, IdOf(displays, projectorNow), output: ProjectorRect1080, applied: ProjectorRect1080);
        Assert.Equal((SafetyActionKind.Refit, projectorNow), (d.Kind, d.Area));
    }

    [Fact]
    public void A_refit_never_crosses_the_panel_monitor_it_pauses_instead()
    {
        // El proyector crece hacia la izquierda y su nuevo rectángulo pisa el monitor del panel.
        var newRect = new ScreenRect(1000, 0, 2840, 1080);
        var displays = Build((new(PanelRect, PanelPath), 1, true), (new(newRect, ProjectorPath), 1, false));
        var d = Eval(displays, IdOf(displays, newRect), output: ProjectorRect1080, applied: ProjectorRect1080);
        Assert.Equal((SafetyActionKind.Pause, OutputPauseReason.PanelCrossed), (d.Kind, d.Reason));
    }

    [Fact]
    public void A_refit_is_never_decided_for_a_target_that_hosts_the_panel()
    {
        var displays = Build((new(PanelRect, PanelPath), 1, false), (new(new ScreenRect(1920, 0, 1280, 720), ProjectorPath), 1, true));
        var d = Eval(displays, IdOf(displays, new ScreenRect(1920, 0, 1280, 720)), output: ProjectorRect1080, applied: ProjectorRect1080);
        Assert.Equal((SafetyActionKind.Pause, OutputPauseReason.PanelCrossed), (d.Kind, d.Reason));
    }

    [Fact]
    public void A_window_dragged_onto_the_panel_still_pauses_instead_of_being_pulled_back()
    {
        var displays = Build((new(PanelRect, PanelPath), 1, true), (new(ProjectorRect1080, ProjectorPath), 1, false));
        var d = Eval(displays, IdOf(displays, ProjectorRect1080), output: new ScreenRect(500, 0, 1920, 1080), applied: ProjectorRect1080);
        Assert.Equal((SafetyActionKind.Pause, OutputPauseReason.PanelCrossed), (d.Kind, d.Reason));
    }

    [Fact]
    public void A_stale_window_rect_overlapping_the_panel_during_a_resolution_change_is_refitted_not_paused()
    {
        // La ventana (rectángulo viejo) pisa el panel porque el monitor se redujo y Windows la recolocó; el rectángulo NUEVO del monitor no lo pisa.
        var newRect = new ScreenRect(1920, 0, 1280, 720);
        var displays = Build((new(PanelRect, PanelPath), 1, true), (new(newRect, ProjectorPath), 1, false));
        var d = Eval(displays, IdOf(displays, newRect), output: new ScreenRect(1000, 0, 1920, 1080), applied: ProjectorRect1080);
        Assert.Equal(SafetyActionKind.Refit, d.Kind);
    }

    [Fact]
    public void After_the_refit_the_applied_area_matches_and_nothing_more_is_decided()
    {
        var newRect = new ScreenRect(1920, 0, 1280, 720);
        var displays = Build((new(PanelRect, PanelPath), 1, true), (new(newRect, ProjectorPath), 1, false));
        Assert.Equal(SafetyActionKind.None, Eval(displays, IdOf(displays, newRect), output: newRect, applied: newRect).Kind);
    }

    [Fact]
    public void A_paused_output_never_refits_it_resumes_in_the_new_rectangle_when_allowed()
    {
        var newRect = new ScreenRect(1920, 0, 1280, 720);
        var displays = Build((new(PanelRect, PanelPath), 1, true), (new(newRect, ProjectorPath), 1, false));
        var d = Eval(displays, IdOf(displays, newRect), output: new ScreenRect(-32000, -32000, 160, 28), applied: ProjectorRect1080, paused: true, reason: OutputPauseReason.DisplayLost);
        Assert.Equal(SafetyActionKind.Resume, d.Kind);
    }

    // ───────────── antirrebote ─────────────

    [Fact]
    public void The_debouncer_waits_for_the_same_rectangle_in_consecutive_polls()
    {
        var d = new RefitDebouncer(2);
        var a = new ScreenRect(1920, 0, 1280, 720);
        Assert.False(d.Observe(a));
        Assert.True(d.Observe(a));
    }

    [Fact]
    public void An_intermediate_rectangle_restarts_the_count()
    {
        var d = new RefitDebouncer(2);
        Assert.False(d.Observe(new ScreenRect(1920, 0, 800, 600)));
        Assert.False(d.Observe(new ScreenRect(1920, 0, 1280, 720)));   // cambió otra vez: aún no es estable
        Assert.True(d.Observe(new ScreenRect(1920, 0, 1280, 720)));
    }

    [Fact]
    public void Nothing_pending_resets_the_debouncer()
    {
        var d = new RefitDebouncer(2);
        var a = new ScreenRect(1920, 0, 1280, 720);
        Assert.False(d.Observe(a));
        Assert.False(d.Observe(null));
        Assert.False(d.Observe(a));   // vuelve a empezar
    }
}
