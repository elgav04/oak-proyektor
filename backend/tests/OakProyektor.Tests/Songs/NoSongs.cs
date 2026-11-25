using OakProyektor.Core.Songs;
using OakProyektor.Data.Repositories;

namespace OakProyektor.Tests.Songs;

/// <summary>Un repositorio de canciones que no guarda nada: toda operación responde «no existe». Para comprobar solo que los handlers están registrados.</summary>
internal sealed class NoSongs : ISongRepository
{
    private static SongException Nothing() => new(SongErrorCodes.NotFound, "no hay canciones");

    public Task<SongDetail?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult<SongDetail?>(null);
    public Task<SongListResult> ListAsync(CancellationToken cancellationToken) => Task.FromResult(new SongListResult([], false));
    public Task<SongSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken) => Task.FromResult(new SongSearchResult(query, false, []));
    public Task<SongSaveResult> SaveAsync(SongSaveRequest request, CancellationToken cancellationToken) => throw Nothing();
    public Task<IReadOnlyList<SongRevisionInfo>> GetHistoryAsync(long songId, CancellationToken cancellationToken) => throw Nothing();
    public Task<SongSaveResult> RestoreRevisionAsync(long songId, long? revisionId, int expectedRevision, CancellationToken cancellationToken) => throw Nothing();
    public Task<SongDeleteResult> DeleteAsync(long id, CancellationToken cancellationToken) => throw Nothing();
}
