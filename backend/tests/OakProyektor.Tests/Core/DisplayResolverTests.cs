using OakProyektor.Core.Display;

namespace OakProyektor.Tests.Core;

/// <summary>I-B: solo <c>null</c> significa «automático»; un ID explícito ausente nunca se sustituye por otro monitor.</summary>
public class DisplayResolverTests
{
    private static readonly DisplayInfo Panel = new(DisplayId.From(0, 0, 1920, 1080), 0, 0, 1920, 1080, 1.25, true, true);
    private static readonly DisplayInfo Projector = new(DisplayId.From(1920, 0, 1920, 1080), 1920, 0, 1920, 1080, 1, false, false);
    private static readonly DisplayInfo Third = new(DisplayId.From(3840, 0, 1280, 720), 3840, 0, 1280, 720, 1, false, false);

    [Fact]
    public void Null_is_automatic_and_picks_the_first_monitor_that_does_not_host_the_panel()
    {
        var r = DisplayResolver.Resolve([Panel, Projector, Third], null);
        Assert.Equal((DisplayResolutionKind.Automatic, Projector.Id), (r.Kind, r.Display!.Id));
    }

    [Fact]
    public void Automatic_with_only_the_panel_monitor_has_no_candidate_so_the_output_goes_in_a_normal_window()
    {
        var r = DisplayResolver.Resolve([Panel], null);
        Assert.Equal(DisplayResolutionKind.Automatic, r.Kind);
        Assert.Null(r.Display);
    }

    [Fact]
    public void An_explicit_id_that_exists_is_chosen_even_if_it_hosts_the_panel()
    {
        var r = DisplayResolver.Resolve([Panel, Projector], Panel.Id);
        Assert.Equal((DisplayResolutionKind.Chosen, Panel.Id), (r.Kind, r.Display!.Id));
    }

    [Fact]
    public void An_explicit_id_that_is_gone_is_not_found_never_replaced_by_another_monitor()
    {
        // El proyector se desconectó; queda otro monitor libre. «Reanudar salida» NO puede abrirse en él.
        var r = DisplayResolver.Resolve([Panel, Third], Projector.Id);
        Assert.Equal(DisplayResolutionKind.NotFound, r.Kind);
        Assert.Null(r.Display);
    }

    [Fact]
    public void An_explicit_id_is_not_found_with_three_monitors_too()
    {
        // Con tres monitores el automático SÍ tendría candidato: precisamente por eso un ID ausente no puede caer al automático.
        var r = DisplayResolver.Resolve([Panel, Projector, Third], DisplayId.From(1920, 0, 1280, 720));   // el proyector volvió con otra resolución
        Assert.Equal(DisplayResolutionKind.NotFound, r.Kind);
        Assert.Null(r.Display);
    }

    [Fact]
    public void An_empty_id_is_an_explicit_request_not_automatic()
    {
        Assert.Equal(DisplayResolutionKind.NotFound, DisplayResolver.Resolve([Panel, Projector], "").Kind);
    }

    [Fact]
    public void The_monitor_is_found_by_id_whatever_the_list_order()
    {
        Assert.Equal(Projector.Id, DisplayResolver.Resolve([Third, Projector, Panel], Projector.Id).Display!.Id);
    }

    [Fact]
    public void An_empty_list_resolves_nothing_without_throwing()
    {
        Assert.Equal(DisplayResolutionKind.NotFound, DisplayResolver.Resolve([], Projector.Id).Kind);
        Assert.Null(DisplayResolver.Resolve([], null).Display);
    }
}
