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
    /// de los elementos faltantes (canciones y presentaciones borradas); nunca presentarlos como si estuvieran.
    /// Los PASAJES no son «faltantes» nunca: se guardan por referencia canónica (libro 1 a 66, capítulo, versículos y la abreviatura de la Biblia
    /// como texto) y al cargar el servicio se resuelven con <see cref="PassageResolver.ResolvePlaylistAsync"/>; si la Biblia no está instalada se muestra
    /// «Biblia no instalada» y se ofrece la Biblia por defecto, sin perder el pasaje.
    /// </summary>
    Task<IReadOnlyList<PlaylistItem>> GetItemsAsync(long playlistId, CancellationToken cancellationToken);
}