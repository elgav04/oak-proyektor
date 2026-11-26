using OakProyektor.Core.Library;
using OakProyektor.Core.Playlists;
using OakProyektor.Data.Repositories;

namespace OakProyektor.Tests.Playlists;

/// <summary>Un repositorio de listas que no guarda nada: toda operación responde «no existe». Para comprobar solo que los handlers están registrados.</summary>
internal sealed class NoPlaylists : IPlaylistRepository
{
    private static PlaylistException Nothing() => new(PlaylistErrorCodes.NotFound, "no hay listas");

    public Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<Playlist>>([]);
    public Task<Playlist?> GetAsync(long id, CancellationToken cancellationToken) => Task.FromResult<Playlist?>(null);
    public Task<IReadOnlyList<PlaylistItem>> GetItemsAsync(long playlistId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<PlaylistItem>>([]);
    public Task<PlaylistListResult> ListAsync(CancellationToken cancellationToken) => Task.FromResult(new PlaylistListResult([], false));
    public Task<PlaylistDetail?> GetDetailAsync(long id, string? defaultBibleAbbreviation, CancellationToken cancellationToken) => Task.FromResult<PlaylistDetail?>(null);
    public Task<PlaylistWriteResult> CreateAsync(string name, string? serviceDate, string? notes, CancellationToken cancellationToken) => throw Nothing();
    public Task<PlaylistWriteResult> UpdateAsync(long id, string name, string? serviceDate, string? notes, int expectedRevision, CancellationToken cancellationToken) => throw Nothing();
    public Task<PlaylistWriteResult> AddItemAsync(long playlistId, PlaylistItemInput item, int? position, CancellationToken cancellationToken) => throw Nothing();
    public Task<PlaylistWriteResult> MoveItemAsync(long playlistId, long itemId, int toIndex, int expectedRevision, CancellationToken cancellationToken) => throw Nothing();
    public Task<PlaylistWriteResult> RemoveItemAsync(long playlistId, long itemId, int expectedRevision, CancellationToken cancellationToken) => throw Nothing();
    public Task<PlaylistWriteResult> UpdateItemAsync(long playlistId, long itemId, PlaylistItemUpdate update, int expectedRevision, CancellationToken cancellationToken) => throw Nothing();
    public Task<PlaylistDuplicateResult> DuplicateAsync(long id, string name, string? serviceDate, CancellationToken cancellationToken) => throw Nothing();
    public Task<PlaylistDeleteResult> DeleteAsync(long id, int expectedRevision, CancellationToken cancellationToken) => throw Nothing();
}
