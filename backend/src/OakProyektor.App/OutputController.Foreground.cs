using Microsoft.Extensions.Logging;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App;

// Video en primer plano (capa entre el contenido y las superposiciones, protocolo v18). Toda la lógica de transición vive en ForegroundEngine (Core, pura y probada); aquí solo hay el pegamento:
// tomar el candado del estado, aplicar y publicar. Nada de esto toca la máquina de estados de la pausa, la política de seguridad ni el sondeo de monitores: la reacción a la pausa solo LEE el
// indicador que el snapshot ya informa (igual que DropAlertIfPaused). El audio de las PISTAS no pasa por aquí (AudioService).
internal sealed partial class OutputController
{
    private readonly ForegroundEngine _foreground = new();   // solo bajo _gate

    private int MasterVolumePct => _settings.Current.AudioConfig.MasterVolumePct;

    /// <summary>La salida NO mostraría el video ahora: sin ventana, con la salida en pausa, o con Texto limpio, Negro o Logo. Un video oculto nunca suena. Solo bajo _gate.</summary>
    private bool ForegroundHiddenLocked() => _window is null || _pause.IsPaused || OutputState.MasterHidesForeground(_state.Master);

    public void PlayForeground(ForegroundRequest request, long order = 0)
    {
        if (order <= 0) order = _clock.Next();
        ForegroundOutcome outcome;
        lock (_gate)
        {
            outcome = _foreground.Play(_state, request, order, ForegroundHiddenLocked());
            if (outcome.Applied) _state = outcome.State;
        }
        if (!outcome.Applied) { _logger.LogInformation("Se ignoró un «reproducir»: el operador pausó o calló después de pedirlo"); return; }
        _logger.LogInformation("Video {MediaId} cargado en primer plano ({Phase})", request.MediaId, outcome.State.Foreground!.Phase);
        Publish();
    }

    public void PauseForeground(long order = 0)
    {
        if (order <= 0) order = _clock.Next();
        lock (_gate) { var o = _foreground.Pause(_state, order); if (o.Applied) _state = o.State; }
        Publish();
    }

    public void ResumeForeground(long order = 0)
    {
        if (order <= 0) order = _clock.Next();
        lock (_gate)
        {
            if (_state.Foreground is null) throw new IpcException(ForegroundErrorCodes.NothingPlaying, "No hay ningún video en primer plano.");
            // Reanudar un video que la salida no mostraría lo haría sonar a oscuras (o con la salida en pausa): se rechaza con el motivo.
            if (ForegroundHiddenLocked())
                throw new IpcException(ForegroundErrorCodes.OutputHidden, _window is null
                    ? "La salida está cerrada: ábrela para reproducir el video."
                    : _pause.IsPaused ? "La salida está en pausa: reanúdala para reproducir el video." : "La salida está en Negro, Texto limpio o Logo: vuelve a «En vivo» para reproducir el video.");
            var o = _foreground.Resume(_state, order);
            if (o.Applied) _state = o.State;
        }
        Publish();
    }

    public void SeekForeground(long positionMs)
    {
        lock (_gate) _state = _foreground.Seek(_state, positionMs);
        Publish();
    }

    public void SetForegroundVolume(int? clipVolumePct, bool? muted)
    {
        lock (_gate) _state = ForegroundEngine.SetVolume(_state, clipVolumePct, muted, MasterVolumePct);
        Publish();
    }

    /// <summary>El volumen maestro cambió en los ajustes: recalcula el efectivo del video actual.</summary>
    public void ApplyMasterVolume()
    {
        lock (_gate) _state = ForegroundEngine.ApplyMaster(_state, MasterVolumePct);
        Publish();
    }

    public void StopForeground(long order = 0)
    {
        if (order <= 0) order = _clock.Next();
        lock (_gate) { var o = _foreground.Stop(_state, order); if (o.Applied) _state = o.State; }
        Publish();
    }

    /// <summary>
    /// «Silencio» (atajo global Ctrl+Alt+Shift+S): PAUSA el video en primer plano y recuerda el número de llegada. Nunca reanuda ni quita nada. Solo toma el candado de memoria y publica: la
    /// página de salida (otro proceso) lo aplica aunque el panel esté atascado. No toma el candado del ciclo de vida ni espera al hilo de UI.
    /// </summary>
    public void SilenceForeground(long order = 0)
    {
        if (order <= 0) order = _clock.Next();
        lock (_gate) _state = _foreground.StopAll(_state, order);
        Publish();
    }

    /// <summary>
    /// «Texto limpio, Negro y Logo ya pausan el video (OutputState.WithMaster)»; esto cubre la PAUSA de la salida y la ventana cerrada: reacciona a lo que el snapshot ya informa
    /// (<c>window.isPaused</c> / sin ventana) y pausa el video, conservando su posición. No modifica ni consulta nada más. Nunca lanza.
    /// </summary>
    private void PauseForegroundIfHidden()
    {
        try
        {
            bool paused;
            lock (_gate)
            {
                paused = _state.Foreground is { Phase: ForegroundPhase.Playing } && (_window is null || _pause.IsPaused);
                if (paused) _state = ForegroundEngine.PauseNow(_state);
            }
            if (!paused) return;
            _logger.LogInformation("El video en primer plano se pausó porque la salida no está visible");
            _publisher.Publish(BuildSnapshot);
        }
        catch (Exception ex) { _logger.LogError(ex, "No se pudo pausar el video al ocultarse la salida"); }
    }

    /// <summary>
    /// La SALIDA informa de la posición y el estado reales del video <c>Id</c> (≈ 4 por segundo). Un informe de un video que ya no es el actual se ignora. El avance va al panel en
    /// <c>media.progress</c> (fuera del snapshot); «terminó» aplica el «al terminar» (quitar o quedarse) y un error quita el video y avisa al panel.
    /// </summary>
    public void ReportForegroundProgress(MediaReportProgressPayload report)
    {
        ForegroundMedia? fg;
        lock (_gate) fg = _state.Foreground;
        if (fg is null || !string.Equals(fg.Id, report.Id, StringComparison.Ordinal)) return;

        switch (report.State)
        {
            case ReportedPlayback.Ended:
                lock (_gate) _state = ForegroundEngine.Ended(_state, report.Id);
                _logger.LogInformation("El video {MediaId} terminó ({OnEnd})", fg.MediaId, fg.OnEnd);
                Publish();
                break;
            case ReportedPlayback.Error:
                lock (_gate) { if (_state.Foreground?.Id == report.Id) _state = _state.WithForeground(null); }
                var code = report.ErrorCode is { Length: > 0 and <= 40 } c ? c : "decodeError";
                _logger.LogWarning("El video {MediaId} no pudo reproducirse: {Code}", fg.MediaId, code);
                _outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.MediaPlaybackFailed, new MediaPlaybackFailedPayload(report.Id, code, DescribePlaybackFailure(code))), Targets.Control);
                Publish();
                return;
        }
        _outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.MediaProgress,
            new MediaProgressPayload(PlaybackTarget.Foreground, report.Id, Math.Max(0, report.PositionMs), Math.Max(0, report.DurationMs), report.State == ReportedPlayback.Playing)), Targets.Control);
    }

    /// <summary>Texto para el operador de un fallo de reproducción del video (la salida manda solo el código; nunca nombres ni rutas).</summary>
    internal static string DescribePlaybackFailure(string code) => code switch
    {
        "unsupportedFormat" => "Este equipo no puede reproducir ese video (formato o códec no soportado, por ejemplo HEVC). Conviértelo a MP4 H.264.",
        "decodeError" => "No se pudo reproducir el video: el archivo está dañado o incompleto.",
        "readError" => "No se pudo leer el archivo del video.",
        "stalled" => "El video se atascó y se quitó de la pantalla.",
        _ => "No se pudo reproducir el video.",
    };
}
