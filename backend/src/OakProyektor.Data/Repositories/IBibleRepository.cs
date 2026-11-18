using OakProyektor.Core.Library;

namespace OakProyektor.Data.Repositories;

public interface IBibleRepository
{
    Task<IReadOnlyList<Bible>> GetBiblesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<Book>> GetBooksAsync(long bibleId, CancellationToken cancellationToken);
    Task<IReadOnlyList<Verse>> GetVersesAsync(long bookId, int chapter, CancellationToken cancellationToken);
    /// <summary>Búsqueda de texto (FTS). Las referencias tipo "Jn 3:16" las resuelve un parser aparte.</summary>
    Task<IReadOnlyList<Verse>> SearchVersesAsync(string query, int limit, CancellationToken cancellationToken);
}
