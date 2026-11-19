namespace OakProyektor.Ipc;

/// <summary>
/// Mensajes de prioridad alta: se procesan en su propio carril y NUNCA esperan detrás de comandos lentos (p. ej. <c>output.open</c>,
/// que crea una ventana). Son los controles maestros y la recuperación de emergencia: deben responder al instante
/// (<c>CLAUDE.md</c>: «Controles maestros… prioridad sobre cualquier transición»).
/// Consecuencia a tener en cuenta: el orden entre carriles NO está garantizado; dentro de cada carril sí (FIFO).
/// </summary>
public static class IpcPriority
{
    public static bool IsHigh(IpcEnvelope envelope) =>
        envelope.Kind == IpcKind.Command && envelope.Type is IpcTypes.OutputSetMaster or IpcTypes.OutputRecover;
}
