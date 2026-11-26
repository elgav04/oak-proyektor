namespace OakProyektor.Ipc;

public static class IpcKind
{
    public const string Command = "command";
    public const string Event = "event";
    public const string Query = "query";
    public const string Reply = "reply";
}

/// <summary>Nombres de mensaje. Deben coincidir con <c>frontend/src/ipc/protocol.ts</c> (lo verifica un test de contrato).</summary>
public static class IpcTypes
{
    public const string DisplayList = "display.list";
    public const string OutputGetState = "output.getState";
    public const string DatabaseGetStatus = "database.getStatus";
    public const string SettingsGetStatus = "settings.getStatus";
    public const string OutputOpen = "output.open";
    public const string OutputClose = "output.close";
    public const string OutputSetMaster = "output.setMaster";
    public const string OutputRecover = "output.recover";
    public const string OutputSetBackground = "output.setBackground";
    public const string OutputShowContent = "output.showContent";
    public const string OutputClearContent = "output.clearContent";
    public const string OutputRetryBackground = "output.retryBackground";
    public const string OutputReportBackgroundFailure = "output.reportBackgroundFailure";
    public const string OutputBackgroundFailed = "output.backgroundFailed";
    public const string SettingsSetAutoRestore = "settings.setAutoRestore";
    public const string SettingsSetDisplayLostPolicy = "settings.setDisplayLostPolicy";
    public const string SettingsSetPanelAlertOnOutput = "settings.setPanelAlertOnOutput";
    public const string DisplayChanged = "display.changed";
    public const string OutputStateChanged = "output.stateChanged";
    public const string CommandFailed = "command.failed";
    public const string DatabaseStatusChanged = "database.statusChanged";
    public const string BibleImport = "bible.import";
    public const string BibleSetAttribution = "bible.setAttribution";
    public const string BibleList = "bible.list";
    public const string BibleLookup = "bible.lookup";
    public const string BibleChapter = "bible.chapter";
    public const string BibleSearch = "bible.search";
    public const string BibleHistory = "bible.history";
    public const string BibleSuggest = "bible.suggest";
    public const string BibleImportProgress = "bible.importProgress";
    public const string BibleImportFinished = "bible.importFinished";
    public const string BibleGetSettings = "bible.getSettings";
    public const string BibleSetSettings = "bible.setSettings";
    public const string SongSearch = "song.search";
    public const string SongList = "song.list";
    public const string SongGet = "song.get";
    public const string SongParseText = "song.parseText";
    public const string SongHistory = "song.history";
    public const string SongGetSettings = "song.getSettings";
    public const string SongSetSettings = "song.setSettings";
    public const string SongSave = "song.save";
    public const string SongRestoreRevision = "song.restoreRevision";
    public const string SongDelete = "song.delete";
    public const string SongChanged = "song.changed";
    public const string PlaylistList = "playlist.list";
    public const string PlaylistGet = "playlist.get";
    public const string PlaylistGetSettings = "playlist.getSettings";
    public const string PlaylistSetSettings = "playlist.setSettings";
    public const string PlaylistCreate = "playlist.create";
    public const string PlaylistUpdate = "playlist.update";
    public const string PlaylistAddItem = "playlist.addItem";
    public const string PlaylistMoveItem = "playlist.moveItem";
    public const string PlaylistRemoveItem = "playlist.removeItem";
    public const string PlaylistUpdateItem = "playlist.updateItem";
    public const string PlaylistDuplicate = "playlist.duplicate";
    public const string PlaylistDelete = "playlist.delete";
    public const string PlaylistChanged = "playlist.changed";
    public const string AlertShow = "alert.show";
    public const string AlertHide = "alert.hide";
    public const string AlertGetSettings = "alert.getSettings";
    public const string AlertSetSettings = "alert.setSettings";
    public const string AlertList = "alert.list";
    public const string AlertSave = "alert.save";
    public const string AlertDelete = "alert.delete";
    public const string AlertChanged = "alert.changed";
}

public sealed record MessageDescriptor(string Type, string Kind);

public static class IpcCatalog
{
    /// <summary>
    /// v2: snapshots con <c>seq</c> global creciente; monitores por ID (no por índice); replies con <c>{ ok, result, error }</c>;
    /// evento <c>command.failed</c>; <c>output.setMaster</c> y <c>output.recover</c> van por un carril de prioridad (ver <see cref="IpcPriority"/>).
    /// v3: <c>output.recover</c> lleva <c>{ reason: "manual" | "emergency" }</c> (el atajo de respaldo de la salida se distingue del botón del panel).
    /// v4: <c>OutputWindowStatus</c> lleva <c>preferredDisplayId</c> y <c>onDisplayLost</c>; nuevo comando <c>settings.setDisplayLostPolicy</c>;
    /// motivos de pausa nuevos <c>duplicateMode</c> y <c>unstable</c>.
    /// v5: <c>OutputWindowStatus</c> lleva <c>panelUnresponsive</c>, <c>panelAlertOnOutput</c> y <c>hotkeys</c> (atajos globales de respaldo y si se pudieron registrar);
    /// nuevo comando <c>settings.setPanelAlertOnOutput</c>.
    /// v6: módulo de Biblias: comandos <c>bible.import</c> y <c>bible.setAttribution</c>; queries <c>bible.list</c>, <c>bible.lookup</c>, <c>bible.chapter</c>,
    /// <c>bible.search</c>, <c>bible.history</c> y <c>bible.suggest</c>; eventos <c>bible.importProgress</c> y <c>bible.importFinished</c> (sin texto bíblico).
    /// v7: motor multimedia por capas: <c>OutputState</c> pasa a llevar <c>schemaVersion</c>, <c>background</c>, <c>content</c> y <c>overlays</c> (ya no <c>backgroundColor</c> ni <c>text</c>);
    /// comandos <c>output.setBackground</c>, <c>output.showContent</c>, <c>output.clearContent</c> y <c>output.reportBackgroundFailure</c> (este último lo manda la salida);
    /// evento <c>output.backgroundFailed</c>; errores <c>output.mediaRejected</c> y <c>output.invalidContent</c>.
    /// v8: configuración de la proyección de Biblia: query <c>bible.getSettings</c> y comando <c>bible.setSettings</c> (Biblia por defecto, nombres cortos, tema, opciones y crossfade; en <c>settings.json</c>);
    /// error <c>bible.invalidSettings</c>.
    /// v9: <c>BackgroundLayer</c> lleva <c>retry</c> (contador de reintentos del mismo medio); nuevo comando <c>output.retryBackground</c> («Reintentar» el fondo que falló).
    /// v11: <c>bible.import</c> lleva <c>allowLargeDeletion</c> y <c>allowLargeChange</c> (las protecciones de la reimportación, que solo se saltan de forma explícita) y su informe lleva
    /// <c>previousVerses</c>, <c>backupPath</c>, <c>numberingProfileKept</c>, <c>discardedVerses</c> y <c>duplicateChaptersDifferent</c>; un <c>numberingProfile</c> nulo ahora CONSERVA el perfil guardado.
    /// v10: query <c>settings.getStatus</c> (avisos de la carga de <c>settings.json</c>: bloques inválidos que volvieron a su valor por defecto o archivo dañado).
    /// v12: módulo de canciones: queries <c>song.search</c>, <c>song.list</c>, <c>song.get</c>, <c>song.parseText</c>, <c>song.history</c> y <c>song.getSettings</c>; comandos <c>song.setSettings</c>,
    /// <c>song.save</c>, <c>song.restoreRevision</c> y <c>song.delete</c>; evento <c>song.changed</c> (resultado de guardar, restaurar o borrar; sin letra). <c>OutputState</c> no cambia.
    /// v13: listas de culto: queries <c>playlist.list</c>, <c>playlist.get</c> y <c>playlist.getSettings</c>; comandos <c>playlist.setSettings</c>, <c>playlist.create</c>, <c>playlist.update</c>,
    /// <c>playlist.addItem</c>, <c>playlist.moveItem</c>, <c>playlist.removeItem</c>, <c>playlist.updateItem</c>, <c>playlist.duplicate</c> y <c>playlist.delete</c>; evento <c>playlist.changed</c>.
    /// <c>output.showContent</c> lleva <c>origin</c> (ids de lista y elemento, o nulo), el <c>Slide</c> lo conserva y <c>OutputState.SchemaVersion</c> pasa a 2.
    /// <c>song.get</c> y <c>song.changed</c> llevan <c>usedInLists</c> (las listas por id, nombre y fecha).
    /// v14: <c>playlist.delete</c> lleva <c>expectedRevision</c> (una vista desactualizada no borra una lista que otra ventana acaba de cambiar; falla con <c>playlist.conflict</c>).
    /// v16: `AlertSettingsPayload` y `AlertDisplaySettings` llevan `tickerEnabled` (interruptor de seguridad del desplazamiento; apagado, `alert.show` en modo `ticker` se rechaza).
    /// v15: alertas rápidas (capa 3): <c>OutputState.overlays</c> pasa de <c>{ items }</c> a <c>{ alert }</c> (una <c>AlertOverlay</c> o nulo; <c>schemaVersion</c> 3); comandos <c>alert.show</c> y
    /// <c>alert.hide</c> (carril de PRIORIDAD: solo tocan memoria), <c>alert.setSettings</c>, <c>alert.save</c> y <c>alert.delete</c>; queries <c>alert.getSettings</c> y <c>alert.list</c>;
    /// evento <c>alert.changed</c> (resultado de guardar o borrar; solo ids). Freeze ya NO congela la alerta.
    /// </summary>
    public const int ProtocolVersion = 16;

    public static IReadOnlyList<MessageDescriptor> Messages { get; } =
    [
        new(IpcTypes.OutputOpen, IpcKind.Command),
        new(IpcTypes.OutputClose, IpcKind.Command),
        new(IpcTypes.OutputSetMaster, IpcKind.Command),
        new(IpcTypes.OutputRecover, IpcKind.Command),
        new(IpcTypes.OutputSetBackground, IpcKind.Command),
        new(IpcTypes.OutputShowContent, IpcKind.Command),
        new(IpcTypes.OutputClearContent, IpcKind.Command),
        new(IpcTypes.OutputRetryBackground, IpcKind.Command),
        new(IpcTypes.OutputReportBackgroundFailure, IpcKind.Command),
        new(IpcTypes.OutputBackgroundFailed, IpcKind.Event),
        new(IpcTypes.SettingsSetAutoRestore, IpcKind.Command),
        new(IpcTypes.SettingsSetDisplayLostPolicy, IpcKind.Command),
        new(IpcTypes.SettingsSetPanelAlertOnOutput, IpcKind.Command),
        new(IpcTypes.DisplayList, IpcKind.Query),
        new(IpcTypes.DisplayList, IpcKind.Reply),
        new(IpcTypes.OutputGetState, IpcKind.Query),
        new(IpcTypes.OutputGetState, IpcKind.Reply),
        new(IpcTypes.DisplayChanged, IpcKind.Event),
        new(IpcTypes.OutputStateChanged, IpcKind.Event),
        new(IpcTypes.CommandFailed, IpcKind.Event),
        new(IpcTypes.DatabaseGetStatus, IpcKind.Query),
        new(IpcTypes.DatabaseGetStatus, IpcKind.Reply),
        new(IpcTypes.SettingsGetStatus, IpcKind.Query),
        new(IpcTypes.SettingsGetStatus, IpcKind.Reply),
        new(IpcTypes.DatabaseStatusChanged, IpcKind.Event),
        new(IpcTypes.BibleImport, IpcKind.Command),
        new(IpcTypes.BibleSetAttribution, IpcKind.Command),
        new(IpcTypes.BibleList, IpcKind.Query),
        new(IpcTypes.BibleList, IpcKind.Reply),
        new(IpcTypes.BibleLookup, IpcKind.Query),
        new(IpcTypes.BibleLookup, IpcKind.Reply),
        new(IpcTypes.BibleChapter, IpcKind.Query),
        new(IpcTypes.BibleChapter, IpcKind.Reply),
        new(IpcTypes.BibleSearch, IpcKind.Query),
        new(IpcTypes.BibleSearch, IpcKind.Reply),
        new(IpcTypes.BibleHistory, IpcKind.Query),
        new(IpcTypes.BibleHistory, IpcKind.Reply),
        new(IpcTypes.BibleSuggest, IpcKind.Query),
        new(IpcTypes.BibleSuggest, IpcKind.Reply),
        new(IpcTypes.BibleImportProgress, IpcKind.Event),
        new(IpcTypes.BibleImportFinished, IpcKind.Event),
        new(IpcTypes.BibleGetSettings, IpcKind.Query),
        new(IpcTypes.BibleGetSettings, IpcKind.Reply),
        new(IpcTypes.BibleSetSettings, IpcKind.Command),
        new(IpcTypes.SongSearch, IpcKind.Query),
        new(IpcTypes.SongSearch, IpcKind.Reply),
        new(IpcTypes.SongList, IpcKind.Query),
        new(IpcTypes.SongList, IpcKind.Reply),
        new(IpcTypes.SongGet, IpcKind.Query),
        new(IpcTypes.SongGet, IpcKind.Reply),
        new(IpcTypes.SongParseText, IpcKind.Query),
        new(IpcTypes.SongParseText, IpcKind.Reply),
        new(IpcTypes.SongHistory, IpcKind.Query),
        new(IpcTypes.SongHistory, IpcKind.Reply),
        new(IpcTypes.SongGetSettings, IpcKind.Query),
        new(IpcTypes.SongGetSettings, IpcKind.Reply),
        new(IpcTypes.SongSetSettings, IpcKind.Command),
        new(IpcTypes.SongSave, IpcKind.Command),
        new(IpcTypes.SongRestoreRevision, IpcKind.Command),
        new(IpcTypes.SongDelete, IpcKind.Command),
        new(IpcTypes.SongChanged, IpcKind.Event),
        new(IpcTypes.PlaylistList, IpcKind.Query),
        new(IpcTypes.PlaylistList, IpcKind.Reply),
        new(IpcTypes.PlaylistGet, IpcKind.Query),
        new(IpcTypes.PlaylistGet, IpcKind.Reply),
        new(IpcTypes.PlaylistGetSettings, IpcKind.Query),
        new(IpcTypes.PlaylistGetSettings, IpcKind.Reply),
        new(IpcTypes.PlaylistSetSettings, IpcKind.Command),
        new(IpcTypes.PlaylistCreate, IpcKind.Command),
        new(IpcTypes.PlaylistUpdate, IpcKind.Command),
        new(IpcTypes.PlaylistAddItem, IpcKind.Command),
        new(IpcTypes.PlaylistMoveItem, IpcKind.Command),
        new(IpcTypes.PlaylistRemoveItem, IpcKind.Command),
        new(IpcTypes.PlaylistUpdateItem, IpcKind.Command),
        new(IpcTypes.PlaylistDuplicate, IpcKind.Command),
        new(IpcTypes.PlaylistDelete, IpcKind.Command),
        new(IpcTypes.PlaylistChanged, IpcKind.Event),
        new(IpcTypes.AlertShow, IpcKind.Command),
        new(IpcTypes.AlertHide, IpcKind.Command),
        new(IpcTypes.AlertGetSettings, IpcKind.Query),
        new(IpcTypes.AlertGetSettings, IpcKind.Reply),
        new(IpcTypes.AlertSetSettings, IpcKind.Command),
        new(IpcTypes.AlertList, IpcKind.Query),
        new(IpcTypes.AlertList, IpcKind.Reply),
        new(IpcTypes.AlertSave, IpcKind.Command),
        new(IpcTypes.AlertDelete, IpcKind.Command),
        new(IpcTypes.AlertChanged, IpcKind.Event),
    ];
}



