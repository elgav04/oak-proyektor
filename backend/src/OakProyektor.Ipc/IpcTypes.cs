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
    public const string SettingsSetAutoRestore = "settings.setAutoRestore";
    public const string DisplayChanged = "display.changed";
    public const string OutputStateChanged = "output.stateChanged";
    public const string CommandFailed = "command.failed";
    public const string DatabaseStatusChanged = "database.statusChanged";
}

public sealed record MessageDescriptor(string Type, string Kind);

public static class IpcCatalog
{
    /// <summary>
    /// v2: snapshots con <c>seq</c> global creciente; monitores por ID (no por índice); replies con <c>{ ok, result, error }</c>;
    /// evento <c>command.failed</c>; <c>output.setMaster</c> y <c>output.recover</c> van por un carril de prioridad (ver <see cref="IpcPriority"/>).
    /// v3: <c>output.recover</c> lleva <c>{ reason: "manual" | "emergency" }</c> (el atajo de respaldo de la salida se distingue del botón del panel).
    /// </summary>
    public const int ProtocolVersion = 3;

    public static IReadOnlyList<MessageDescriptor> Messages { get; } =
    [
        new(IpcTypes.OutputOpen, IpcKind.Command),
        new(IpcTypes.OutputClose, IpcKind.Command),
        new(IpcTypes.OutputSetMaster, IpcKind.Command),
        new(IpcTypes.OutputRecover, IpcKind.Command),
        new(IpcTypes.SettingsSetAutoRestore, IpcKind.Command),
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
    ];
}


