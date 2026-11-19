using OakProyektor.Core.Library;

namespace OakProyektor.Data.Repositories;

/// <remarks>
/// Toda implementación debe abrir sus conexiones con <see cref="DatabaseReadinessExtensions.OpenWhenReadyAsync"/>: así espera
/// a que la BD esté inicializada (o falla con <see cref="DatabaseUnavailableException"/> si la inicialización falló).
/// </remarks>
public interface IPlaylistRepository
{
    Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken);
    Task<Playlist?> GetAsync(long id, CancellationToken cancellationToken);
    /// <summary>
    /// Elementos del servicio en orden, con <see cref="PlaylistItem.IsMissing"/>. Quien abre el servicio DEBE avisar al operador
    /// de los elementos faltantes; nunca presentarlos como si estuvieran.
    /// </summary>
    Task<IReadOnlyList<PlaylistItem>> GetItemsAsync(long playlistId, CancellationToken cancellationToken);
}