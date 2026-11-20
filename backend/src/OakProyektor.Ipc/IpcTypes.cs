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
    public const string OutputOpen = "output.open";
    public const string OutputClose = "output.close";
    public const string OutputSetMaster = "output.setMaster";
    public const string OutputRecover = "output.recover";
    public const string OutputSetBackground = "output.setBackground";
    public const string OutputShowContent = "output.showContent";
    public const string OutputClearContent = "output.clearContent";
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
    /// </summary>
    public const int ProtocolVersion = 7;

    public static IReadOnlyList<MessageDescriptor> Messages { get; } =
    [
        new(IpcTypes.OutputOpen, IpcKind.Command),
        new(IpcTypes.OutputClose, IpcKind.Command),
        new(IpcTypes.OutputSetMaster, IpcKind.Command),
        new(IpcTypes.OutputRecover, IpcKind.Command),
        new(IpcTypes.OutputSetBackground, IpcKind.Command),
        new(IpcTypes.OutputShowContent, IpcKind.Command),
        new(IpcTypes.OutputClearContent, IpcKind.Command),
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
    ];
}



