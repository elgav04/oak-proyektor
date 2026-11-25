using OakProyektor.Core.Songs;

namespace OakProyektor.Data.Repositories;

/// <summary>
/// Canciones: secciones con etiqueta, orden de reproducción, copias previas por canción y búsqueda. Toda implementación abre sus conexiones con
/// <see cref="DatabaseReadinessExtensions.OpenWhenReadyAsync"/> y las escrituras son UNA transacción. Los fallos esperados son <see cref="SongException"/> con los códigos de
/// <see cref="SongErrorCodes"/>; ningún mensaje de error ni registro lleva letra de canciones.
/// </summary>
public interface ISongRepository
{
    Task<SongDetail?> GetAsync(long id, CancellationToken cancellationToken);

    /// <summary>Todas las canciones (resúmenes, sin letra) ordenadas por título sin distinguir tildes ni mayúsculas; como mucho <see cref="SongLimits.MaxListed"/>.</summary>
    Task<SongListResult> ListAsync(CancellationToken cancellationToken);

    /// <summary>Búsqueda por título, autor y letra (FTS5, sin tildes). Palabras sueltas = todas deben aparecer (la última por prefijo); entre comillas = frase exacta.</summary>
    Task<SongSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Crea o actualiza una canción. Al actualizar: comprueba la revisión (<c>song.conflict</c> si caducó, sin cambiar nada), guarda la COPIA PREVIA (las últimas 20) y aplica
    /// los cambios en su sitio, todo en UNA transacción.
    /// </summary>
    Task<SongSaveResult> SaveAsync(SongSaveRequest request, CancellationToken cancellationToken);

    /// <summary>Copias previas de la canción, la más reciente primero (sin texto).</summary>
    Task<IReadOnlyList<SongRevisionInfo>> GetHistoryAsync(long songId, CancellationToken cancellationToken);

    /// <summary>
    /// Restaura una copia previa (<paramref name="revisionId"/> nulo = la más reciente: «Deshacer último guardado»). Antes toma una copia del estado actual (motivo
    /// <c>restore</c>), así que también se puede deshacer. Falla con <c>song.conflict</c> si <paramref name="expectedRevision"/> ya no es la vigente.
    /// </summary>
    Task<SongSaveResult> RestoreRevisionAsync(long songId, long? revisionId, int expectedRevision, CancellationToken cancellationToken);

    /// <summary>
    /// Borra la canción (sin papelera). Misma política que <c>PlaylistItems</c>: no se bloquea por estar en servicios, pero tampoco deja referencias rotas en silencio:
    /// los elementos de servicio quedan con su título e <c>IsMissing</c>. Devuelve en cuántos servicios estaba.
    /// </summary>
    Task<SongDeleteResult> DeleteAsync(long id, CancellationToken cancellationToken);
}
