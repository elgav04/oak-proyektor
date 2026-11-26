using System.Text.Json;
using OakProyektor.App;
using OakProyektor.Core;
using OakProyektor.Core.Output;

namespace OakProyektor.Tests.Alerts;

/// <summary>Carga por bloque del bloque <c>alertDisplay</c> de settings.json (como la Biblia y las canciones): un valor inválido vuelve a SU valor por defecto con un aviso.</summary>
public class AlertSettingsTests
{
    private static (AppSettings? Settings, List<string> Warnings) Parse(string json)
    {
        var warnings = new List<string>();
        return (SettingsStore.Parse(json, warnings), warnings);
    }

    [Fact]
    public void A_file_without_the_block_uses_the_defaults_and_warns_nothing()
    {
        var (s, w) = Parse("{}");
        Assert.Empty(w);
        Assert.Null(s!.AlertDisplay);
        Assert.Equal((AlertMode.Band, 30), (s.Alerts.Mode, s.Alerts.DurationSec));
        Assert.Equal(AlertTheme.Default, s.Alerts.EffectiveTheme);
    }

    [Fact]
    public void A_valid_block_is_loaded_as_written()
    {
        var theme = AlertTheme.Default with { FontSize = 60, Position = AlertPosition.Top };
        var json = $$"""{ "alertDisplay": { "mode": "ticker", "durationSec": 120, "theme": {{JsonSerializer.Serialize(theme, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } })}} } }""";
        var (s, w) = Parse(json);
        Assert.Empty(w);
        Assert.Equal((AlertMode.Ticker, 120, theme), (s!.Alerts.Mode, s.Alerts.DurationSec, s.Alerts.EffectiveTheme));
    }

    [Fact]
    public void An_invalid_theme_goes_back_to_the_default_theme_without_losing_the_mode_and_duration()
    {
        var (s, w) = Parse("""{ "alertDisplay": { "mode": "ticker", "durationSec": 60, "theme": { "fontFamily": "Arial", "fontSize": 52, "color": "azul", "outlineWidth": 0, "outlineColor": "#000000", "bandColor": "#000000D9", "align": "center", "position": "bottom", "paddingY": 22, "maxLines": 2, "autoFit": true, "minFontSize": 31, "tickerSpeed": 180 } } }""");
        Assert.Contains(w, m => m.Contains("tema de las alertas"));
        Assert.Equal((AlertMode.Ticker, 60), (s!.Alerts.Mode, s.Alerts.DurationSec));
        Assert.Equal(AlertTheme.Default, s.Alerts.EffectiveTheme);
    }

    [Fact]
    public void An_invalid_duration_goes_back_to_30_seconds_and_keeps_the_rest()
    {
        var (s, w) = Parse("""{ "alertDisplay": { "mode": "ticker", "durationSec": 3 } }""");
        Assert.Contains(w, m => m.Contains("duración de las alertas"));
        Assert.Equal((AlertMode.Ticker, 30), (s!.Alerts.Mode, s.Alerts.DurationSec));
    }

    [Fact]
    public void An_unreadable_block_goes_back_to_the_defaults_with_a_warning_and_the_other_blocks_survive()
    {
        var (s, w) = Parse("""{ "autoRestoreOutput": false, "alertDisplay": { "mode": "volando" } }""");
        Assert.Contains(w, m => m.Contains("alertDisplay"));
        Assert.Equal(AlertMode.Band, s!.Alerts.Mode);
        Assert.False(s.AutoRestoreOutput);
    }
}
