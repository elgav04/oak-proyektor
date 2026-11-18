using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Core;

public class OutputStateTests
{
    [Fact]
    public void Starts_black_with_no_text_at_revision_zero()
    {
        var s = OutputState.Initial;
        Assert.Equal((0L, MasterMode.Black, "#000000", (string?)null), (s.Revision, s.Master, s.BackgroundColor, s.Text));
    }

    [Fact]
    public void Live_shows_test_text_and_clear_text_keeps_only_the_background()
    {
        var live = OutputState.Initial.WithMaster(MasterMode.Live);
        Assert.Equal("Texto de prueba", live.Text);

        var cleared = live.WithMaster(MasterMode.ClearText);
        Assert.Null(cleared.Text);
        Assert.Equal(live.BackgroundColor, cleared.BackgroundColor);
    }

    [Fact]
    public void Black_and_logo_change_the_visuals()
    {
        var black = OutputState.Initial.WithMaster(MasterMode.Live).WithMaster(MasterMode.Black);
        Assert.Equal(("#000000", (string?)null), (black.BackgroundColor, black.Text));

        var logo = black.WithMaster(MasterMode.Logo);
        Assert.Equal("LOGO", logo.Text);
    }

    [Fact]
    public void Freeze_keeps_what_was_on_screen_but_is_a_new_revision()
    {
        var live = OutputState.Initial.WithMaster(MasterMode.Live);
        var frozen = live.WithMaster(MasterMode.Freeze);

        Assert.Equal(MasterMode.Freeze, frozen.Master);
        Assert.Equal(live.BackgroundColor, frozen.BackgroundColor);
        Assert.Equal(live.Text, frozen.Text);
        Assert.Equal(live.Revision + 1, frozen.Revision);
    }

    [Fact]
    public void Repeating_the_same_master_changes_nothing()
    {
        var live = OutputState.Initial.WithMaster(MasterMode.Live);
        Assert.Same(live, live.WithMaster(MasterMode.Live));
    }

    [Fact]
    public void Revision_only_grows()
    {
        var s = OutputState.Initial;
        long last = s.Revision;
        foreach (var mode in new[] { MasterMode.Live, MasterMode.Logo, MasterMode.Freeze, MasterMode.Black, MasterMode.ClearText })
        {
            s = s.WithMaster(mode);
            Assert.True(s.Revision > last);
            last = s.Revision;
        }
    }

    [Fact]
    public void Unknown_master_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OutputState.Initial.WithMaster((MasterMode)99));
    }
}
