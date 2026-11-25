using OakProyektor.Core.Library;
using OakProyektor.Core.Playlists;

namespace OakProyektor.Data.Repositories;

/// <summary>
/// Listas de culto. Toda implementación abre sus conexiones con <see cref="DatabaseReadinessExtensions.OpenWhenReadyAsync"/> (espera a la BD o falla con
/// <see cref="DatabaseUnavailableException"/>). Las escrituras son comandos PEQUEÑOS y atómicos (una transacción cada uno) y los fallos esperados son <see cref="PlaylistException"/>
/// con los códigos de <see cref="PlaylistErrorCodes"/>. Ningún mensaje de error ni registro lleva títulos, notas ni letra: solo ids y conteos.
/// <para><b>Una vista desactualizada no puede borrar ni mover elementos</b>: cada lista tiene una <c>Revision</c> que sube con CADA cambio (también al añadir); mover, quitar, editar un
/// elemento o cambiar los datos de la lista exigen la revisión vigente (<c>playlist.conflict</c> si caducó, sin cambiar nada). Añadir un elemento no la exige: no puede destruir nada.</para>
/// <para><b>Elementos faltantes</b> (canción borrada): no se pierden, <c>IsMissing</c> y <c>Projectable = false</c>; la navegación los salta; duplicar una lista los omite y cuenta cuántos.
/// <b>Pasajes</b> se resuelven al leer (<see cref="PassageResolver"/>): una Biblia ausente nunca los pierde.</para>
/// </summary>
public interface IPlaylistRepository
{
    // ───────────── lectura heredada (modelos planos de la BD) ─────────────
    Task<IReadOnlyList<Playlist>> GetAllAsync(CancellationToken cancellationToken);
    Task<Playlist?> GetAsync(long id, CancellationToken cancellationToken);
    /// <summary>Elementos en orden con <see cref="PlaylistItem.IsMissing"/>. Quien abre el servicio DEBE avisar de los faltantes; los pasajes se resuelven con <see cref="PassageResolver.ResolvePlaylistAsync"/>.</summary>
    Task<IReadOnlyList<PlaylistItem>> GetItemsAsync(long playlistId, CancellationToken cancellationToken);

    // ───────────── lectura del módulo ─────────────
    /// <summary>Listas con su número de elementos y de faltantes; las que tienen fecha primero (la más reciente arriba), luego las plantillas (sin fecha). Como mucho <see cref="PlaylistLimits.MaxListed"/>.</summary>
    Task<PlaylistListResult> ListAsync(CancellationToken cancellationToken);

    /// <summary>La lista con sus elementos, el título ACTUAL de cada canción y los pasajes resueltos (<paramref name="defaultBibleAbbreviation"/> = la Biblia por defecto, para ofrecerla si falta la preferida).</summary>
    Task<PlaylistDetail?> GetDetailAsync(long id, string? defaultBibleAbbreviation, CancellationToken cancellationToken);

    // ───────────── escritura (carril lento) ─────────────
    Task<PlaylistWriteResult> CreateAsync(string name, string? serviceDate, string? notes, CancellationToken cancellationToken);

    /// <summary>Cambia nombre, fecha y notas. Exige la revisión vigente.</summary>
    Task<PlaylistWriteResult> UpdateAsync(long id, string name, string? serviceDate, string? notes, int expectedRevision, CancellationToken cancellationToken);

    /// <summary>Añade un elemento (al final, o en <paramref name="position"/>: 0 a número de elementos). No exige revisión; sí sube la revisión. Máximo <see cref="PlaylistLimits.MaxItems"/> (<c>playlist.full</c>).</summary>
    Task<PlaylistWriteResult> AddItemAsync(long playlistId, PlaylistItemInput item, int? position, CancellationToken cancellationToken);

    /// <summary>Mueve el elemento a <paramref name="toIndex"/> (0 a número de elementos − 1). Exige la revisión vigente.</summary>
    Task<PlaylistWriteResult> MoveItemAsync(long playlistId, long itemId, int toIndex, int expectedRevision, CancellationToken cancellationToken);

    Task<PlaylistWriteResult> RemoveItemAsync(long playlistId, long itemId, int expectedRevision, CancellationToken cancellationToken);

    /// <summary>Edita una nota (título, texto, limpiar al activar) o la Biblia preferida de un pasaje. Exige la revisión vigente.</summary>
    Task<PlaylistWriteResult> UpdateItemAsync(long playlistId, long itemId, PlaylistItemUpdate update, int expectedRevision, CancellationToken cancellationToken);

    /// <summary>Copia la lista (orden, notas, pasajes y notas) con otro nombre y fecha. Los elementos FALTANTES no se copian (no se pueden crear sin su registro): se cuentan en <c>Skipped</c>.</summary>
    Task<PlaylistDuplicateResult> DuplicateAsync(long id, string name, string? serviceDate, CancellationToken cancellationToken);

    Task<PlaylistDeleteResult> DeleteAsync(long id, CancellationToken cancellationToken);
}
