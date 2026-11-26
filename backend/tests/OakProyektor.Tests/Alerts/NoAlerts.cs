using OakProyektor.Core.Alerts;
using OakProyektor.Data.Alerts;

namespace OakProyektor.Tests.Alerts;

/// <summary>Repositorio de alertas vacío (sin BD): para los tests que solo comprueban que cada mensaje del catálogo tiene su handler.</summary>
internal sealed class NoAlerts : IAlertRepository
{
    public Task<SavedAlertList> ListAsync(CancellationToken cancellationToken) => Task.FromResult(new SavedAlertList([], []));
    public Task<SavedAlert> SaveAsync(long? id, string text, bool pinned, CancellationToken cancellationToken) => throw new AlertException(AlertErrorCodes.InvalidContent, "sin base de datos");
    public Task<bool> DeleteAsync(long id, CancellationToken cancellationToken) => Task.FromResult(false);
}

internal sealed class NoAlertRecorder : IAlertRecorder
{
    public void Record(string normalizedText) { }
}
