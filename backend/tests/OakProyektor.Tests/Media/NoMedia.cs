using OakProyektor.Core.Media;
using OakProyektor.Data.Media;

namespace OakProyektor.Tests.Media;

/// <summary>Biblioteca vacía (sin BD): para los tests que solo comprueban que cada mensaje del catálogo tiene su handler.</summary>
internal sealed class NoMedia : IMediaRepository
{
    public Task<MediaListResult> ListAsync(MediaQuery query, CancellationToken ct) => Task.FromResult(new MediaListResult([], [], false, 0, 0));
    public Task<MediaItem?> GetAsync(long id, CancellationToken ct) => Task.FromResult<MediaItem?>(null);
    public Task<MediaItem?> FindByShaAsync(string sha256, CancellationToken ct) => Task.FromResult<MediaItem?>(null);
    public Task<MediaItem> InsertAsync(NewMediaItem item, CancellationToken ct) => throw new MediaException(MediaErrorCodes.InvalidRequest, "sin base de datos");
    public Task<MediaItem> RenameAsync(long id, string name, CancellationToken ct) => throw new MediaException(MediaErrorCodes.NotFound, "no existe");
    public Task<MediaItem> MoveAsync(long id, long? folderId, CancellationToken ct) => throw new MediaException(MediaErrorCodes.NotFound, "no existe");
    public Task<MediaDeleteResult> DeleteAsync(long id, CancellationToken ct) => Task.FromResult(new MediaDeleteResult(false, null, null, false));
    public Task<MediaItem> ApplyProbeAsync(long id, bool ok, long? durationMs, int? width, int? height, string? failureCode, CancellationToken ct) => throw new MediaException(MediaErrorCodes.NotFound, "no existe");
    public Task SetThumbnailAsync(long id, bool has, CancellationToken ct) => throw new MediaException(MediaErrorCodes.NotFound, "no existe");
    public Task<IReadOnlyList<MediaItem>> AllAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<MediaItem>>([]);
    public Task SetStatusAsync(IReadOnlyCollection<long> ids, MediaStatus status, string? code, CancellationToken ct) => Task.CompletedTask;
    public Task<MediaFolder> CreateFolderAsync(string name, CancellationToken ct) => throw new MediaException(MediaErrorCodes.InvalidRequest, "sin base de datos");
    public Task<MediaFolder> RenameFolderAsync(long id, string name, CancellationToken ct) => throw new MediaException(MediaErrorCodes.FolderNotFound, "no existe");
    public Task DeleteFolderAsync(long id, CancellationToken ct) => Task.CompletedTask;
}
