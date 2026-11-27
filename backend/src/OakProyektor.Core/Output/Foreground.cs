namespace OakProyektor.Core.Output;

/// <summary>Estado de reproducción del video en primer plano que ORDENA el backend (la salida reporta el avance real aparte). Camel case: playing, paused, ended.</summary>
public enum ForegroundPhase
{
    Playing,
    Paused,
    /// <summary>Terminó con «quedarse» (<see cref="ForegroundEndAction.Stay"/>): queda en su último fotograma, pausado.</summary>
    Ended,
}

/// <summary>Qué se ve detrás de las franjas de un video que no es 16:9 (<c>black</c> por defecto: el fondo animado no se asoma).</summary>
public enum ForegroundBackdrop
{
    Black,
    Background,
}

/// <summary>Qué hace la salida cuando el video termina. En la v18: <c>back</c> = sale con fundido y se descubre lo que había debajo; <c>stay</c> = queda en pausa en su último fotograma.</summary>
public enum ForegroundEndAction
{
    Back,
    Stay,
}

/// <summary>
/// Capa de primer plano (protocolo v18, <c>OutputState.SchemaVersion</c> 4): un video con audio entre el contenido y las superposiciones. El fondo en bucle NO se toca: sigue debajo.
/// <see cref="Url"/> es una URL <c>file:///</c> de la COPIA local de la biblioteca, ya validada por el backend. <see cref="VolumePct"/> es el volumen EFECTIVO (el del clip ya multiplicado
/// por el volumen maestro): la salida solo lo aplica. <see cref="SeekMs"/>/<see cref="SeekSeq"/>: «ir a esta posición» se aplica cuando <c>SeekSeq</c> cambia (dos «buscar» a la misma
/// posición se distinguen); <c>SeekSeq</c> 0 = ninguno. La posición real, la duración y el fin los REPORTA la salida (<c>media.reportProgress</c>); no forman parte del snapshot.
/// </summary>
public sealed record ForegroundMedia(
    string Id, long MediaId, string Url, string FileName,
    ForegroundPhase Phase, int ClipVolumePct, int VolumePct, bool Muted, bool Loop,
    long SeekMs, int SeekSeq, int FadeInMs, int FadeOutMs,
    ForegroundBackdrop Backdrop, ForegroundEndAction OnEnd)
{
    public const int MaxFadeMs = 5000;
    public const int DefaultFadeMs = 400;

    /// <summary>Volumen efectivo = volumen del clip × volumen maestro (ambos 0–100), redondeado.</summary>
    public static int Effective(int clipPct, int masterPct) => (int)Math.Round(Math.Clamp(clipPct, 0, 100) * Math.Clamp(masterPct, 0, 100) / 100.0, MidpointRounding.AwayFromZero);
}

public static class ForegroundErrorCodes
{
    public const string NotVideo = "media.notVideo";
    public const string NotAudio = "media.notAudio";
    public const string NotPlayable = "media.notPlayable";
    public const string OutputHidden = "media.outputHidden";
    public const string InvalidRequest = "media.invalidPlayback";
    public const string NothingPlaying = "media.nothingPlaying";
}

/// <summary>Lo que <c>media.play</c> pide para el primer plano (ya validado por el handler).</summary>
public sealed record ForegroundRequest(
    long MediaId, string Url, string FileName, bool StartPlaying, int ClipVolumePct, int MasterVolumePct, bool Muted, bool Loop,
    int FadeInMs, int FadeOutMs, ForegroundBackdrop Backdrop, ForegroundEndAction OnEnd);

/// <param name="Applied">false = se ignoró (una orden POSTERIOR del operador, p. ej. «Silencio», ya llegó); el estado no cambió.</param>
public sealed record ForegroundOutcome(OutputState State, bool Applied);

/// <summary>
/// Transiciones del video en primer plano: lógica PURA, sin hilos ni reloj (el controlador la llama bajo el candado de su estado). Las órdenes del operador se ordenan por su NÚMERO DE
/// LLEGADA (<see cref="TransportOrder"/>): la última intención gana aunque viajen por carriles distintos. «Silencio» (<see cref="StopAll"/>) solo PAUSA: nunca reanuda ni quita el video, y un
/// «reproducir» que ya estaba encolado en otro carril no puede hacer sonar lo que el operador acaba de callar.
/// </summary>
public sealed class ForegroundEngine
{
    private long _idCounter;
    private readonly TransportOrder _order = new();

    /// <summary>
    /// Carga y (si procede) arranca un video. <paramref name="hidden"/> = la salida no lo mostraría (Negro, Texto limpio, Logo o pausa de la salida): se carga en PAUSA, nunca suena
    /// oculto. Cada llamada es una reproducción nueva (id nuevo: la salida reinicia el elemento).
    /// </summary>
    public ForegroundOutcome Play(OutputState state, ForegroundRequest r, long order, bool hidden)
    {
        // Cargar «listo» (sin arrancar) no es una orden de sonar: solo se ordena si se pidió arrancar.
        if (r.StartPlaying && !_order.AllowPlay(order)) return new ForegroundOutcome(state, false);
        var phase = r.StartPlaying && !hidden ? ForegroundPhase.Playing : ForegroundPhase.Paused;
        var fg = new ForegroundMedia(
            $"f{++_idCounter}", r.MediaId, r.Url, r.FileName, phase, r.ClipVolumePct, ForegroundMedia.Effective(r.ClipVolumePct, r.MasterVolumePct), r.Muted, r.Loop,
            0, 0, r.FadeInMs, r.FadeOutMs, r.Backdrop, r.OnEnd);
        return new ForegroundOutcome(state.WithForeground(fg), true);
    }

    /// <summary>Pausa por orden del operador (idempotente). Conserva la posición: la salida mantiene el mismo elemento.</summary>
    public ForegroundOutcome Pause(OutputState state, long order) =>
        _order.AllowHalt(order) ? new ForegroundOutcome(PauseNow(state), true) : new ForegroundOutcome(state, false);

    /// <summary>Pausa por una causa interna (un control maestro o la pausa de la salida): no es una orden del operador, así que no entra en el orden de llegada.</summary>
    public static OutputState PauseNow(OutputState state) =>
        state.Foreground is { Phase: ForegroundPhase.Playing } fg ? state.WithForeground(fg with { Phase = ForegroundPhase.Paused }) : state;

    /// <summary>
    /// Reanuda. Desde «terminado» vuelve a empezar (sube <c>SeekSeq</c> a 0 ms, para que la salida lo reinicie aunque el navegador lo deje al final).
    /// Ignorado si una orden de pausa o «Silencio» posterior ya llegó.
    /// </summary>
    public ForegroundOutcome Resume(OutputState state, long order)
    {
        if (!_order.AllowPlay(order) || state.Foreground is not { } fg) return new ForegroundOutcome(state, false);
        if (fg.Phase == ForegroundPhase.Playing) return new ForegroundOutcome(state, true);
        var next = fg.Phase == ForegroundPhase.Ended ? fg with { Phase = ForegroundPhase.Playing, SeekMs = 0, SeekSeq = fg.SeekSeq + 1 } : fg with { Phase = ForegroundPhase.Playing };
        return new ForegroundOutcome(state.WithForeground(next), true);
    }

    public OutputState Seek(OutputState state, long positionMs) =>
        state.Foreground is { } fg ? state.WithForeground(fg with { SeekMs = Math.Max(0, positionMs), SeekSeq = fg.SeekSeq + 1 }) : state;

    /// <summary>Cambia el volumen del clip y/o el silencio; el efectivo se recalcula con el maestro.</summary>
    public static OutputState SetVolume(OutputState state, int? clipPct, bool? muted, int masterPct) =>
        state.Foreground is { } fg
            ? state.WithForeground(fg with { ClipVolumePct = clipPct ?? fg.ClipVolumePct, VolumePct = ForegroundMedia.Effective(clipPct ?? fg.ClipVolumePct, masterPct), Muted = muted ?? fg.Muted })
            : state;

    /// <summary>El volumen maestro cambió: recalcula el efectivo del clip actual.</summary>
    public static OutputState ApplyMaster(OutputState state, int masterPct) =>
        state.Foreground is { } fg ? state.WithForeground(fg with { VolumePct = ForegroundMedia.Effective(fg.ClipVolumePct, masterPct) }) : state;

    /// <summary>Quita el video (la salida hace el fundido de salida con el <c>FadeOutMs</c> que ya tenía). Ignorado si un «reproducir» posterior ya se aplicó.</summary>
    public ForegroundOutcome Stop(OutputState state, long order) =>
        _order.AllowHalt(order) ? new ForegroundOutcome(state.WithForeground(null), true) : new ForegroundOutcome(state, false);

    /// <summary>Silencio: PAUSA el video (sin quitarlo) y recuerda el número de llegada para ignorar un «reproducir» anterior ya encolado.</summary>
    public OutputState StopAll(OutputState state, long order)
    {
        _order.AllowHalt(order);
        return PauseNow(state);
    }

    /// <summary>
    /// La salida informa de que el video <paramref name="id"/> terminó (sin bucle). Un informe de un video que ya no es el actual se ignora. <c>Back</c> lo quita; <c>Stay</c> lo deja en pausa
    /// en su último fotograma (<see cref="ForegroundPhase.Ended"/>).
    /// </summary>
    public static OutputState Ended(OutputState state, string id)
    {
        if (state.Foreground is not { } fg || fg.Id != id || fg.Phase == ForegroundPhase.Ended) return state;
        return fg.OnEnd == ForegroundEndAction.Back ? state.WithForeground(null) : state.WithForeground(fg with { Phase = ForegroundPhase.Ended });
    }
}