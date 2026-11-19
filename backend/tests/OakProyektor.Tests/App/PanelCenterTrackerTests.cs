using OakProyektor.App;

namespace OakProyektor.Tests.App;

/// <summary>M10: el panel minimizado no debe quedar «en ningún monitor».</summary>
public class PanelCenterTrackerTests
{
    [Fact]
    public void A_normal_position_gives_its_center()
    {
        var t = new PanelCenterTracker();
        Assert.Equal((960, 540), t.Update(240, 170, 1440, 740, isMinimized: false));
    }

    [Fact]
    public void When_minimized_it_keeps_the_last_known_position_instead_of_the_parking_spot()
    {
        var t = new PanelCenterTracker();
        t.Update(2000, 100, 1000, 600, false);                                    // el panel estaba en el segundo monitor

        var minimized = t.Update(-32000, -32000, 160, 28, isMinimized: true);     // Windows lo aparca fuera de todos los monitores

        Assert.Equal((2500, 400), minimized);
    }

    [Theory]
    [InlineData(-32000, -32000)]
    [InlineData(-30000, 100)]
    [InlineData(100, -30001)]
    public void Parking_coordinates_are_never_taken_as_a_real_position_even_if_the_minimized_flag_is_missing(int x, int y)
    {
        var t = new PanelCenterTracker();
        t.Update(500, 300, 800, 600, false);

        Assert.Equal((900, 600), t.Update(x, y, 160, 28, isMinimized: false));
    }

    [Fact]
    public void It_follows_the_panel_again_once_it_is_restored_and_moved()
    {
        var t = new PanelCenterTracker();
        t.Update(0, 0, 1000, 600, false);
        t.Update(-32000, -32000, 160, 28, true);

        Assert.Equal((3000, 500), t.Update(2500, 200, 1000, 600, false));        // restaurado en otro monitor
    }

    [Fact]
    public void If_it_started_minimized_nothing_is_known_yet()
    {
        Assert.Null(new PanelCenterTracker().Update(-32000, -32000, 160, 28, true));   // el proveedor usará el monitor principal
    }

    [Fact]
    public void A_window_legitimately_placed_at_a_negative_position_on_a_left_monitor_is_still_tracked()
    {
        var t = new PanelCenterTracker();
        Assert.Equal((-1000, 500), t.Update(-1920, 200, 1840, 600, false));            // monitor a la izquierda del principal: coordenadas negativas reales
    }
}