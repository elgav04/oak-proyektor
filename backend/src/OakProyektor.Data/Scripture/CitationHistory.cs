using System.Threading.Channels;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OakProyektor.Core.Scripture;

namespace OakProyektor.Data.Scripture;

/// <summary>Una cita por anotar en el historial: la referencia COMPLETA (todos los tramos) y su forma canónica. Solo referencias, nunca texto bíblico.</summary>
/// <param name="Limit">Entradas que conserva el historial (ya acotado; 0 = no se guarda nada).</param>
public sealed record CitationToRecord(string Bible, ParsedReference Reference, string Label, int Limit);

/// <summary>
/// Quien anota las citas del historial. <see cref="Record"/> NUNCA lanza ni espera al disco en las implementaciones de fondo: lo que no se pueda anotar se descarta con registro
/// (el historial es una comodidad; proyectar no puede depender de él).
/// </summary>
public interface ICitationRecorder
{
    void Record(CitationToRecord citation);
}

/// <summary>Lectura y escritura del historial de citas. Las escrituras son UNA transacción: la fila, sus tramos y el recorte al límite.</summary>
public static class CitationHistoryStore
{
    /// <summary>
    /// Anota la cita (moviéndola al principio si ya estaba, por su forma canónica) con TODOS sus tramos y recorta el historial al límite. Con límite 0 no guarda nada.
    /// <paramref name="commandTimeoutSeconds"/> acota cuánto espera cada comando a que otro escritor suelte el bloqueo (null = lo normal de la conexión, hasta 30 s).
    /// </summary>
    public static async Task WriteAsync(SqliteConnection connection, CitationToRecord c, int? commandTimeoutSeconds, CancellationToken ct)
    {
        if (c.Limit <= 0) return;
        var first = c.Reference.Segments[0];
        // DIFERIDA a propósito: el BEGIN por defecto de Microsoft.Data.Sqlite es IMMEDIATE y espera el bloqueo de escritura (30 s, sin que el tiempo del comando lo acorte; medido).
        // Diferida, el BEGIN no bloquea y la primera escritura sí respeta <paramref name="commandTimeoutSeconds"/>.
        await using var transaction = connection.BeginTransaction(deferred: true);
        await connection.ExecuteAsync(new CommandDefinition("DELETE FROM CitationHistory WHERE BibleAbbreviation = @bible AND Label = @label",
            new { bible = c.Bible, label = c.Label }, transaction, commandTimeout: commandTimeoutSeconds, cancellationToken: ct)).ConfigureAwait(false);   // sus tramos se van en cascada
        var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "INSERT INTO CitationHistory (BibleAbbreviation, BookNumber, Chapter, VerseStart, VerseEnd, Label) VALUES (@bible, @book, @chapter, @start, @end, @label); SELECT last_insert_rowid();",
            new { bible = c.Bible, book = c.Reference.BookNumber, chapter = first.StartChapter, start = first.StartVerse, end = first.StartChapter == first.EndChapter ? first.EndVerse : null, label = c.Label },
            transaction, commandTimeout: commandTimeoutSeconds, cancellationToken: ct)).ConfigureAwait(false);
        var position = 0;
        foreach (var s in c.Reference.Segments)
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO CitationHistorySegments (HistoryId, Position, StartChapter, StartVerse, EndChapter, EndVerse) VALUES (@id, @position, @sc, @sv, @ec, @ev)",
                new { id, position = position++, sc = s.StartChapter, sv = s.StartVerse, ec = s.EndChapter, ev = s.EndVerse },
                transaction, commandTimeout: commandTimeoutSeconds, cancellationToken: ct)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM CitationHistory WHERE Id NOT IN (SELECT Id FROM CitationHistory ORDER BY CitedAt DESC, Id DESC LIMIT @limit)",
            new { limit = c.Limit }, transaction, commandTimeout: commandTimeoutSeconds, cancellationToken: ct)).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Los tramos de una entrada del historial. Las anotadas desde la migración 8 los tienen guardados; las anteriores no, y se leen de su <c>Label</c> (la forma canónica,
    /// que <see cref="ReferenceParser"/> interpreta) y, si no se pudiera, de las columnas de la propia fila (solo el primer tramo, lo único que guardaban).
    /// </summary>
    public static IReadOnlyList<ReferenceSegment> SegmentsOf(IReadOnlyList<ReferenceSegment>? stored, int bookNumber, string label, int chapter, int? verseStart, int? verseEnd)
    {
        if (stored is { Count: > 0 }) return stored;
        var parsed = ReferenceParser.Parse(label);
        if (parsed.Reference is { } reference && reference.BookNumber == bookNumber) return reference.Segments;
        return [new ReferenceSegment(chapter, verseStart, chapter, verseStart is null ? null : verseEnd ?? verseStart)];
    }

    /// <summary>¿Algún tramo contiene este versículo (del libro ya comprobado)?</summary>
    public static bool Contains(IEnumerable<ReferenceSegment> segments, int chapter, int verse)
    {
        foreach (var s in segments)
        {
            if (chapter < s.StartChapter || chapter > s.EndChapter) continue;
            if (s.IsWholeChapters) return true;
            if (chapter == s.StartChapter && verse < s.StartVerse) continue;
            if (chapter == s.EndChapter && verse > s.EndVerse) continue;
            return true;
        }
        return false;
    }
}

/// <summary>
/// Anota el historial EN SEGUNDO PLANO (H5). El carril en vivo del protocolo no puede esperar al bloqueo de escritura de SQLite (una importación de Biblia lo retiene
/// segundos): <see cref="Record"/> solo encola y vuelve al instante; un único consumidor escribe con su propia conexión y un tiempo de espera corto
/// (<see cref="WriteTimeout"/>). Si no se logra (bloqueo, BD no lista, disco…) la cita se DESCARTA con un aviso en el log: nunca se reintenta en bucle ni se acumula (la cola
/// guarda como mucho <see cref="Capacity"/> y, si se llena, descarta la más antigua).
/// </summary>
public sealed class BackgroundCitationRecorder : ICitationRecorder, IDisposable
{
    public const int Capacity = 64;

    private readonly ISqliteConnectionFactory connections;
    private readonly IDatabaseReadiness readiness;
    private readonly ILogger<BackgroundCitationRecorder> logger;

    public BackgroundCitationRecorder(ISqliteConnectionFactory connections, IDatabaseReadiness readiness, ILogger<BackgroundCitationRecorder> logger)
    {
        this.connections = connections; this.readiness = readiness; this.logger = logger;
        // Al desbordarse la cola se descarta la más antigua: hay que descontarla de las pendientes.
        _queue = Channel.CreateBounded<CitationToRecord>(
            new BoundedChannelOptions(Capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false },
            _ => { Interlocked.Decrement(ref _pending); Interlocked.Increment(ref _discarded); });
    }

    private readonly Channel<CitationToRecord> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private Task? _consumer;
    private int _pending;
    private int _discarded;

    /// <summary>Cuánto espera como máximo una anotación (en total) antes de descartarse.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Citas descartadas desde el arranque (para las pruebas y el diagnóstico).</summary>
    public int Discarded => Volatile.Read(ref _discarded);

    public void Record(CitationToRecord citation)
    {
        try
        {
            if (citation.Limit <= 0) return;
            lock (_gate) _consumer ??= Task.Run(ConsumeAsync);
            Interlocked.Increment(ref _pending);
            if (!_queue.Writer.TryWrite(citation)) Interlocked.Decrement(ref _pending);   // cerrado (apagado)
        }
        catch (Exception ex) { logger.LogWarning(ex, "No se pudo encolar la anotación del historial"); }   // jamás propagar al carril en vivo
    }

    private async Task ConsumeAsync()
    {
        try
        {
            await foreach (var citation in _queue.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                    timeout.CancelAfter(WriteTimeout);
                    await using var connection = await connections.OpenWhenReadyAsync(readiness, timeout.Token).ConfigureAwait(false);

                    // La espera por el bloqueo de escritura la fija el tiempo de cada COMANDO (CommandTimeout, en segundos): PRAGMA busy_timeout no sirve, Microsoft.Data.Sqlite
                    // lo vuelve a fijar en cada comando (medido: seguía esperando los 30 s normales).
                    await CitationHistoryStore.WriteAsync(connection, citation, Math.Max(1, (int)Math.Ceiling(WriteTimeout.TotalSeconds / 3)), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _discarded);
                    // Solo la referencia (nunca texto bíblico). Un historial que no se pudo anotar no afecta a la proyección.
                    logger.LogWarning(ex, "Se descartó la anotación del historial de {Label} ({Bible}): no se pudo escribir a tiempo", citation.Label, citation.Bible);
                }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }
        catch (OperationCanceledException) { /* apagado */ }
        catch (Exception ex) { logger.LogError(ex, "Falló el anotador del historial"); }
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
