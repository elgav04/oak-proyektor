using OakProyektor.Core.Library;

namespace OakProyektor.Data.Repositories;

public interface ISongRepository
{
    Task<Song?> GetAsync(long id, CancellationToken cancellationToken);
    Task<IReadOnlyList<SongSlide>> GetSlidesAsync(long songId, CancellationToken cancellationToken);
    /// <summary>Búsqueda por título y letra (SongsFts).</summary>
    Task<IReadOnlyList<Song>> SearchAsync(string query, int limit, CancellationToken cancellationToken);
}
