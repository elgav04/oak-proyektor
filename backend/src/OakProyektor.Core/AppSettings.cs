using OakProyektor.Core.Output;
using OakProyektor.Core.Playlists;
using OakProyektor.Core.Scripture;
using OakProyektor.Core.Songs;

namespace OakProyektor.Core;

/// <summary>Configuración persistente (data/settings.json, NO la base de datos de contenido).</summary>
/// <param name="AutoRestoreOutput">Reanudar la salida sola (sin clics) cuando reaparece su monitor o el panel sale de él.</param>
/// <param name="PreferredDisplayId">Monitor preferido de la salida, por ID estable (<c>DisplayId</c>): el que el operador eligió al abrirla. Es al que la salida siempre intenta volver.</param>
/// <param name="OnDisplayLost">Qué hacer si el monitor de la salida desaparece: avisar y esperar (por defecto) o mover sola al mejor monitor que no sea el del panel.</param>
/// <param name="ShowPanelAlertOnOutput">Mostrar «PANEL SIN RESPUESTA» en la propia salida cuando el JS del panel se atasca. Por defecto NO: la proyección no muestra nada.</param>
/// <param name="RecentDisplayIds">Monitores donde se ha mostrado la salida, el más reciente primero (máx. 8): criterio «el último usado» de <c>BestDisplayPicker</c>.</param>
/// <param name="DefaultBible">Abreviatura de la Biblia que se usa si una consulta no indica ninguna; nulo = la primera instalada.</param>
/// <param name="CitationHistoryLimit">Cuántas citas conserva el historial (0 = no guardar historial; máximo 1000). Se edita en <c>settings.json</c>.</param>
/// <param name="BibleDisplay">Tema, opciones y nombres cortos de la proyección de Biblia (v8).</param>
/// <param name="SongDisplay">Tema, crossfade y comportamiento al final de la proyección de canciones (v12).</param>
public sealed record AppSettings(
    bool AutoRestoreOutput = true,
    string? PreferredDisplayId = null,
    DisplayLostPolicy OnDisplayLost = DisplayLostPolicy.WarnAndWait,
    IReadOnlyList<string>? RecentDisplayIds = null,
    bool ShowPanelAlertOnOutput = false,
    string? DefaultBible = null,
    int CitationHistoryLimit = AppSettings.DefaultCitationHistoryLimit,
    BibleDisplaySettings? BibleDisplay = null,
    SongDisplaySettings? SongDisplay = null,
    PlaylistDisplaySettings? PlaylistDisplay = null,
    AlertDisplaySettings? AlertDisplay = null)
{
    public const int DefaultCitationHistoryLimit = 100;

    /// <summary>Modo, duración y tema de las alertas (v15); un <c>settings.json</c> anterior sin este bloque usa los valores por defecto.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public AlertDisplaySettings Alerts => AlertDisplay ?? new AlertDisplaySettings();

    /// <summary>Cómo se opera una lista en vivo (v13); un <c>settings.json</c> anterior sin este bloque usa los valores por defecto.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public PlaylistDisplaySettings Playlists => PlaylistDisplay ?? new PlaylistDisplaySettings();

    /// <summary>Cómo se proyectan las canciones (v12); un <c>settings.json</c> anterior sin este bloque usa los valores por defecto.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public SongDisplaySettings Songs => SongDisplay ?? new SongDisplaySettings();

    /// <summary>Cómo se proyecta la Biblia (v8); un <c>settings.json</c> anterior sin este bloque usa los valores por defecto.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BibleDisplaySettings Bible => BibleDisplay ?? new BibleDisplaySettings();
}
