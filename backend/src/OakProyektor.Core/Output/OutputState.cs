namespace OakProyektor.Core.Output;

/// <summary>
/// Snapshot completo e idempotente de lo que debe mostrar la ventana de salida.
/// <see cref="Revision"/> crece con cada cambio para que las réplicas descarten snapshots viejos.
/// Los colores y textos son de prueba hasta que existan las capas reales.
/// </summary>
public sealed record OutputState(long Revision, MasterMode Master, string BackgroundColor, string? Text)
{
    public static OutputState Initial { get; } = new(0, MasterMode.Black, "#000000", null);

    /// <summary>Aplica un control maestro. Freeze conserva lo que se estaba viendo; repetir el mismo modo no cambia nada.</summary>
    public OutputState WithMaster(MasterMode master)
    {
        if (master == Master) return this;
        return master switch
        {
            MasterMode.Live => new OutputState(Revision + 1, master, "#1e3a8a", "Texto de prueba"),
            MasterMode.ClearText => new OutputState(Revision + 1, master, "#1e3a8a", null),
            MasterMode.Black => new OutputState(Revision + 1, master, "#000000", null),
            MasterMode.Logo => new OutputState(Revision + 1, master, "#111827", "LOGO"),
            MasterMode.Freeze => this with { Revision = Revision + 1, Master = master },
            _ => throw new ArgumentOutOfRangeException(nameof(master), master, "Modo maestro desconocido"),
        };
    }
}
