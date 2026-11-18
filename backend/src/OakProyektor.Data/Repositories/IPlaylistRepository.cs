using OakProyektor.Core.Library;

namespace OakProyektor.Data.Repositories;

public interface IPlaylistRepository
{
    Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken);
    Task<Playlist?> GetAsync(long id, CancellationToken cancellationToken);
}
