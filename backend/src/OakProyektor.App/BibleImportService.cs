using Microsoft.Extensions.Logging;
using OakProyektor.App.Handlers;
using OakProyektor.Data;
using OakProyektor.Data.Scripture;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;

namespace OakProyektor.App;

/// <summary>
/// Importaciones de Biblias pedidas desde el panel (<c>bible.import</c>): UNA a la vez, en un hilo de fondo, con avance (<c>bible.importProgress</c>) y resultado
/// (<c>bible.importFinished</c>) hacia el panel. Nada de lo que se publica lleva texto bíblico. Todo el trabajo de fondo captura sus excepciones.
/// </summary>
internal sealed class BibleImportService(BibleImporter importer, DatabaseOptions database, IOutbox outbox, ILogger<BibleImportService> logger) : IDisposable
{
    private readonly IOutbox _outbox = outbox;
    private readonly ILogger<BibleImportService> _logger = logger;
    private readonly CancellationTokenSource _cancel = new();
    private int _running;

    /// <summary>Arranca la importación y vuelve. Falla (<c>bible.importBusy</c>) si ya hay una en curso.</summary>
    public void Start(BibleImportPayload payload)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new IpcException(BibleErrorCodes.ImportBusy, "Ya hay una importación de Biblia en curso; espera a que termine.");

        _ = Task.Run(async () =>
        {
            try { await RunAsync(payload, _cancel.Token).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogError(ex, "Falló el hilo de importación de la Biblia {Abbreviation}", payload.Abbreviation); }
            finally { Interlocked.Exchange(ref _running, 0); }
        });
    }

    private async Task RunAsync(BibleImportPayload payload, CancellationToken ct)
    {
        var request = new BibleImportRequest(payload.Path, payload.Abbreviation, payload.Name, payload.Language, null, payload.NumberingProfile, payload.Attribution,
            payload.AllowLargeDeletion, payload.AllowLargeChange, database.BackupDirectory);
        BibleImportFinishedPayload finished;
        try
        {
            var report = await importer.ImportAsync(request, new SyncProgress(this, payload.Abbreviation), ct).ConfigureAwait(false);
            finished = new BibleImportFinishedPayload(payload.Abbreviation, true, null, null, BibleIpc.Map(report));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            finished = new BibleImportFinishedPayload(payload.Abbreviation, false, "cancelled", "La importación se canceló al cerrar la aplicación. No se cambió nada.", null);
        }
        catch (BibleException ex)
        {
            _logger.LogWarning("La importación de {Abbreviation} falló: {Code}", payload.Abbreviation, ex.Code);
            finished = new BibleImportFinishedPayload(payload.Abbreviation, false, ex.Code, ex.Message, null);
        }
        catch (DatabaseUnavailableException ex)
        {
            finished = new BibleImportFinishedPayload(payload.Abbreviation, false, "database.unavailable", ex.Message, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "La importación de {Abbreviation} falló", payload.Abbreviation);
            finished = new BibleImportFinishedPayload(payload.Abbreviation, false, BibleErrorCodes.ImportFailed, "La importación falló; revisa el registro de la aplicación. No se cambió nada.", null);
        }
        _outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.BibleImportFinished, finished), Targets.Control);
    }

    /// <summary>Progreso SÍNCRONO (<c>Progress&lt;T&gt;</c> lo despacharía al pool y los eventos podrían llegar desordenados).</summary>
    private sealed class SyncProgress(BibleImportService owner, string abbreviation) : IProgress<ImportProgress>
    {
        public void Report(ImportProgress value)
        {
            try { owner._outbox.Publish(IpcEnvelope.Create(IpcKind.Event, IpcTypes.BibleImportProgress, new BibleImportProgressPayload(abbreviation, value.Phase, value.Done, value.Total)), Targets.Control); }
            catch (Exception ex) { owner._logger.LogWarning(ex, "No se pudo publicar el avance de la importación"); }
        }
    }

    public void Dispose() => _cancel.Cancel();
}
