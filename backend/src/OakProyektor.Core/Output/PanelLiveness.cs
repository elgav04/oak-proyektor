namespace OakProyektor.Core.Output;

/// <summary>Resultado de evaluar al panel: si está sin respuesta y si eso CAMBIÓ desde la evaluación anterior.</summary>
public readonly record struct PanelHealth(bool Unresponsive, bool Changed, TimeSpan Silence);

/// <summary>
/// ¿Sigue vivo el JavaScript del panel? (función pura con un reloj que se pasa). El panel manda un latido cada 4 s (<c>database.getStatus</c>,
/// desde un worker, para que ni el throttling de pestañas ocultas lo retrase); CUALQUIER mensaje suyo cuenta como actividad. Si pasan
/// <paramref name="timeout"/> sin ninguno, el hilo principal de su página está atascado (bucle infinito, trabajo pesado) o la página no cargó.
/// El hilo de UI de C# sigue vivo en ese caso (las ventanas se pueden mover): por eso esto solo se puede saber por el silencio del JS.
/// </summary>
public sealed class PanelLivenessTracker(TimeSpan? timeout = null)
{
    /// <summary>≈ 2,5 latidos perdidos: un latido tardío no debe dar falsas alarmas, y «unos segundos» sigue siendo unos segundos.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;
    private DateTimeOffset _lastActivity;
    private bool _unresponsive;

    /// <summary>La línea base: la página empieza a cargarse (arranque o recarga). Hasta que hable, el silencio se cuenta desde aquí.</summary>
    public void Reset(DateTimeOffset now) => _lastActivity = now;

    /// <summary>Llegó un mensaje del panel.</summary>
    public void Touch(DateTimeOffset now) => _lastActivity = now;

    /// <summary>¿Estaría sin respuesta en <paramref name="now"/>? Solo lectura: no consume el aviso de cambio de <see cref="Evaluate"/>.</summary>
    public bool IsUnresponsiveAt(DateTimeOffset now) => now - _lastActivity > _timeout;

    public PanelHealth Evaluate(DateTimeOffset now)
    {
        var silence = now - _lastActivity;
        var unresponsive = silence > _timeout;
        var changed = unresponsive != _unresponsive;
        _unresponsive = unresponsive;
        return new PanelHealth(unresponsive, changed, silence);
    }
}

/// <summary>
/// Atajos globales de respaldo de los controles maestros (Negro, Limpiar texto, Logo): funcionan aunque el panel esté atascado. Son INTERRUPTORES:
/// pulsar el atajo del modo que ya está activo vuelve a Live; así hay camino de regreso sin tocar el panel.
/// </summary>
public static class MasterShortcut
{
    public static MasterMode Next(MasterMode current, MasterMode requested) => current == requested ? MasterMode.Live : requested;
}
