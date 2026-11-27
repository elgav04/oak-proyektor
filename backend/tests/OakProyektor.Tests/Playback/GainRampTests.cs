using OakProyektor.Core.Audio;

namespace OakProyektor.Tests.Playback;

/// <summary>Fundidos y silencio sin chasquido: <see cref="GainRamp"/> es pura, así que se prueba sin dispositivos de audio.</summary>
public class GainRampTests
{
    private const int Rate = 48_000;

    [Fact]
    public void Starts_silent_and_a_zero_ms_request_is_immediate()
    {
        var ramp = new GainRamp(Rate);
        var buf = Enumerable.Repeat(1f, 8).ToArray();
        ramp.Apply(buf, 2);
        Assert.All(buf, v => Assert.Equal(0f, v));
        ramp.SetTarget(1f, 0);
        buf = [.. Enumerable.Repeat(0.5f, 8)];
        ramp.Apply(buf, 2);
        Assert.All(buf, v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public void A_fade_in_rises_linearly_per_frame_and_reaches_the_target_exactly()
    {
        var ramp = new GainRamp(Rate);
        ramp.SetTarget(1f, 100);   // 100 ms = 4800 cuadros
        var buf = Enumerable.Repeat(1f, 4800 * 2).ToArray();   // estéreo
        ramp.Apply(buf, 2);
        Assert.Equal(buf[0], buf[1]);                                 // los dos canales del mismo cuadro llevan la misma ganancia
        Assert.True(buf[0] > 0f && buf[0] < 0.001f);                  // arranca desde casi 0
        Assert.True(buf[4799 * 2] > 0.99f);                            // y termina en 1
        for (var i = 2; i < buf.Length; i += 2) Assert.True(buf[i] >= buf[i - 2]);   // nunca baja
        Assert.Equal(1f, ramp.Current);
        var next = new[] { 1f, 1f };
        ramp.Apply(next, 2);
        Assert.All(next, v => Assert.Equal(1f, v));
    }

    [Fact]
    public void The_ramp_continues_across_buffers()
    {
        var one = new GainRamp(Rate); one.SetTarget(1f, 100);
        var whole = Enumerable.Repeat(1f, 4800).ToArray();
        one.Apply(whole, 1);

        var two = new GainRamp(Rate); two.SetTarget(1f, 100);
        var a = Enumerable.Repeat(1f, 1000).ToArray();
        var b = Enumerable.Repeat(1f, 3800).ToArray();
        two.Apply(a, 1); two.Apply(b, 1);
        Assert.Equal<float>(whole, a.Concat(b));
    }

    [Fact]
    public void A_silence_fade_of_100_ms_ends_at_exactly_zero()
    {
        var ramp = new GainRamp(Rate, 1f);
        ramp.SetTarget(0f, 100);
        var buf = Enumerable.Repeat(1f, 4800).ToArray();
        ramp.Apply(buf, 1);
        Assert.Equal(0f, buf[^1]);
        Assert.Equal(0f, ramp.Current);
        var after = new[] { 1f, 1f, 1f };
        ramp.Apply(after, 1);
        Assert.All(after, v => Assert.Equal(0f, v));
    }

    [Fact]
    public void A_new_target_in_the_middle_of_a_ramp_goes_from_where_it_is()
    {
        var ramp = new GainRamp(Rate);
        ramp.SetTarget(1f, 100);
        ramp.Apply(new float[2400], 1);   // a mitad de camino
        var middle = ramp.Current;
        Assert.InRange(middle, 0.45f, 0.55f);
        ramp.SetTarget(0f, 50);
        var buf = Enumerable.Repeat(1f, 2400).ToArray();
        ramp.Apply(buf, 1);
        Assert.True(buf[0] <= middle && buf[0] > middle - 0.01f);
        Assert.Equal(0f, buf[^1]);
    }

    [Theory]
    [InlineData(-3f, 0f)]
    [InlineData(7f, 1f)]
    public void The_target_is_clamped_between_zero_and_one(float asked, float expected)
    {
        var ramp = new GainRamp(Rate);
        ramp.SetTarget(asked, 0);
        Assert.Equal(expected, ramp.Target);
    }
}
