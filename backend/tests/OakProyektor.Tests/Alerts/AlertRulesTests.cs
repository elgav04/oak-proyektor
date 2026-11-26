using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Alerts;

/// <summary>Validación y normalización de las alertas. El motivo nunca cita el texto. Todo sintético.</summary>
public class AlertRulesTests
{
    private static string? Normalize(string? raw, out string text) => AlertRules.TryNormalizeText(raw, out text);

    [Fact]
    public void A_text_of_exactly_300_characters_is_valid_and_301_is_rejected()
    {
        Assert.Null(Normalize(new string('a', 300), out var ok));
        Assert.Equal(300, ok.Length);

        var problem = Normalize(new string('a', 301), out var bad);
        Assert.NotNull(problem);
        Assert.Contains("300", problem);
        Assert.Equal("", bad);
    }

    [Fact]
    public void The_limit_counts_the_normalized_text_not_the_raw_one()
    {
        // 300 letras con espacios sobrantes: tras colapsarlos caben.
        var raw = "   " + string.Join("  ", Enumerable.Repeat("a", 150)) + "   ";   // 150 letras + 149 separadores = 299 normalizadas
        Assert.Null(Normalize(raw, out var text));
        Assert.Equal(299, text.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \t\n ")]
    public void Empty_or_blank_text_is_rejected(string? raw)
    {
        Assert.NotNull(Normalize(raw, out var text));
        Assert.Equal("", text);
    }

    [Fact]
    public void Whitespace_is_collapsed_and_trimmed_and_the_text_is_normalized_to_NFC()
    {
        Assert.Null(Normalize("  AVISO \t DE\r\n PRUEBA  1 ", out var text));
        Assert.Equal("AVISO DE PRUEBA 1", text);

        Assert.Null(Normalize("Café de prueba", out var nfc));   // «é» descompuesta → una sola letra
        Assert.Equal("Café de prueba", nfc);
    }

    [Fact]
    public void Control_characters_other_than_whitespace_are_rejected_without_quoting_the_text()
    {
        var problem = Normalize("aviso\u0007 de prueba", out _);
        Assert.NotNull(problem);
        Assert.DoesNotContain("aviso", problem, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_text_key_ignores_case_and_the_normalization_but_not_the_letters()
    {
        Normalize("Aviso   De PRUEBA", out var a);
        Normalize("aviso de prueba", out var b);
        Normalize("aviso de prueba 2", out var c);
        Assert.Equal(AlertRules.TextKey(a), AlertRules.TextKey(b));
        Assert.NotEqual(AlertRules.TextKey(a), AlertRules.TextKey(c));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(5, true)]
    [InlineData(30, true)]
    [InlineData(1800, true)]
    [InlineData(4, false)]
    [InlineData(1801, false)]
    [InlineData(-1, false)]
    public void Duration_is_zero_meaning_until_removed_or_between_5_and_1800_seconds(int seconds, bool valid) =>
        Assert.Equal(valid, AlertRules.ValidateDuration(seconds) is null);

    [Fact]
    public void The_default_theme_and_settings_are_valid_and_the_minimum_size_is_below_the_size()
    {
        Assert.Null(AlertRules.ValidateTheme(AlertTheme.Default));
        Assert.Null(AlertRules.ValidateSettings(new AlertDisplaySettings()));
        Assert.True(AlertTheme.Default.MinFontSize < AlertTheme.Default.FontSize);
        Assert.Equal((AlertMode.Band, AlertRules.DefaultDurationSec, AlertPosition.Bottom, 2), (new AlertDisplaySettings().Mode, new AlertDisplaySettings().DurationSec, AlertTheme.Default.Position, AlertTheme.Default.MaxLines));
    }

    public static IEnumerable<object[]> BadThemes()
    {
        var t = AlertTheme.Default;
        yield return [t with { FontFamily = "Arial; background:url(x)" }];
        yield return [t with { FontFamily = "" }];
        yield return [t with { FontSize = 15 }];
        yield return [t with { FontSize = 201 }];
        yield return [t with { MinFontSize = t.FontSize + 1 }];
        yield return [t with { MinFontSize = 11 }];
        yield return [t with { Color = "red" }];
        yield return [t with { OutlineColor = "#12345" }];
        yield return [t with { BandColor = "rgba(0,0,0,.8)" }];
        yield return [t with { OutlineWidth = 21 }];
        yield return [t with { Align = (TextAlign)9 }];
        yield return [t with { Position = (AlertPosition)9 }];
        yield return [t with { PaddingY = 121 }];
        yield return [t with { MaxLines = 0 }];
        yield return [t with { MaxLines = 4 }];
        yield return [t with { TickerSpeed = 59 }];
        yield return [t with { TickerSpeed = 401 }];
    }

    [Theory]
    [MemberData(nameof(BadThemes))]
    public void An_invalid_theme_is_rejected(AlertTheme theme) => Assert.NotNull(AlertRules.ValidateTheme(theme));

    [Fact]
    public void An_unknown_mode_and_a_missing_theme_are_rejected()
    {
        Assert.NotNull(AlertRules.ValidateMode((AlertMode)7));
        Assert.NotNull(AlertRules.ValidateTheme(null));
        Assert.NotNull(AlertRules.ValidateSettings(new AlertDisplaySettings((AlertMode)7)));
        Assert.NotNull(AlertRules.ValidateSettings(new AlertDisplaySettings(DurationSec: 3)));
    }
}
