using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace OakProyektor.Data.Alerts;

/// <summary>
/// Anota el uso de las alertas EN SEGUNDO PLANO (mismo patrón que el historial de citas). <c>alert.show</c> va por el carril de prioridad y NO toca la base de datos: <see cref="Record"/> solo
/// encola y vuelve al instante; un único consumidor escribe con su propia conexión y un tiempo de espera corto. Si no se logra (bloqueo, BD no lista, disco…) la anotación se DESCARTA
/// con un aviso SIN el texto (solo un conteo): mostrar una alerta nunca depende de la base de datos. La cola guarda como mucho <see cref="Capacity"/> y descarta la más antigua.
/// </summary>
public sealed class BackgroundAlertRecorder : IAlertRecorder, IDisposable
{
    public const int Capacity = 64;

    private readonly ISqliteConnectionFactory _connections;
    private readonly IDatabaseReadiness _readiness;
    private readonly ILogger<BackgroundAlertRecorder> _logger;
    private readonly TimeProvider _clock;
    private readonly Channel<string> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _consumer;
    private int _pending;
    private int _discarded;

    public BackgroundAlertRecorder(ISqliteConnectionFactory connections, IDatabaseReadiness readiness, ILogger<BackgroundAlertRecorder> logger, TimeProvider? clock = null)
    {
        _connections = connections; _readiness = readiness; _logger = logger; _clock = clock ?? TimeProvider.System;
        _queue = Channel.CreateBounded<string>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false },
            _ => { Interlocked.Decrement(ref _pending); Interlocked.Increment(ref _discarded); });
    }

    /// <summary>Cuánto espera como máximo una anotación (en total) antes de descartarse.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Anotaciones descartadas desde el arranque (para las pruebas y el diagnóstico).</summary>
    public int Discarded => Volatile.Read(ref _discarded);

    public void Record(string normalizedText)
    {
        try
        {
            lock (_gate) _consumer ??= Task.Run(ConsumeAsync);
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(normalizedText)) Interlocked.Decrement(ref _pending);   // cerrado (apagado)
        }
        catch (Exception ex) { _logger.LogWarning(ex, "No se pudo encolar la anotación de una alerta"); }   // jamás propagar al carril de prioridad
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var text in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(WriteTimeout);
                    await using var connection = await _connections.OpenWhenReadyAsync(_readiness, timeout.Token).ConfigureAwait(false);
                    await AlertStore.RecordUseAsync(connection, text, _clock.GetUtcNow(), Math.Max(1, (int)Math.Ceiling(WriteTimeout.TotalSeconds / 3)), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _discarded);
                    // Ni el texto ni el título: solo que una anotación no se pudo escribir. Una alerta no anotada no afecta a la proyección.
                    _logger.LogWarning(ex, "Se descartó la anotación del uso de una alerta: no se pudo escribir a tiempo");
                }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }
        catch (OperationCanceledException) { /* apagado */ }
        catch (Exception ex) { _logger.LogError(ex, "Falló el anotador de alertas"); }
    }

    /// <summary>Espera a que se vacíe la cola (o al tiempo dado). Para pruebas y para el apagado.</summary>
    public async Task<bool> WaitIdleAsync(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (Volatile.Read(ref _pending) > 0)
        {
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(10).ConfigureAwait(false);
        }
        return true;
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        _stop.Cancel();
        _stop.Dispose();
    }
}
