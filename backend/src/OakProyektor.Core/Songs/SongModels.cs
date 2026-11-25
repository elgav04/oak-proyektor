namespace OakProyektor.Core.Songs;

/// <summary>Sección guardada de una canción. <c>Kind</c> se deriva de <c>Label</c>; <c>Text</c> lleva una línea en blanco por cada corte manual de diapositiva.</summary>
public sealed record SongSection(long Id, string Kind, string Label, string Text);

/// <summary>Fondo propio de una canción (opcional). <c>Kind</c>: <c>image</c> | <c>video</c> | <c>color</c>. Sin fondo = no se toca el fondo actual de la salida.</summary>
public sealed record SongBackground(string Kind, string? Path, string? Color, bool Muted);

/// <summary>
/// Una canción completa. <c>Arrangement</c> son índices (posiciones) en <c>Sections</c>: el orden de reproducción por defecto, con repeticiones.
/// <c>UsedInServices</c> = en cuántos servicios (listas) está incluida (para avisar antes de borrar).
/// </summary>
public sealed record SongDetail(
    long Id, string Title, string? Author, string? Copyright, string? CcliNumber, string? Notes, int Revision,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, SongBackground? Background,
    IReadOnlyList<SongSection> Sections, IReadOnlyList<int> Arrangement, int UsedInServices)
{
    /// <summary>El texto único con etiquetas (secciones y, si no es el orden natural, la línea «Orden:»).</summary>
    public string Text => SongTextFormatter.Format([.. Sections.Select(s => new ParsedSection(s.Label, s.Kind, s.Text))], Arrangement);
}

/// <param name="Fragment">Trozo con las coincidencias entre « y » (solo en búsquedas; es letra del operador: solo va al panel, nunca a un log).</param>
public sealed record SongSummary(long Id, string Title, string? Author, DateTimeOffset UpdatedAt, string? Fragment);

public sealed record SongListResult(IReadOnlyList<SongSummary> Songs, bool Truncated);

public sealed record SongSearchResult(string Query, bool Truncated, IReadOnlyList<SongSummary> Songs);

/// <summary>Una sección que llega a guardar. <c>Id</c> nulo = sección nueva; con id, se actualiza EN SU SITIO (conserva su identidad). El tipo se ignora: se deriva de la etiqueta.</summary>
public sealed record SongSectionInput(long? Id, string Label, string? Kind, string Text);

/// <summary>Por qué se guarda (motivo de la copia previa de <c>SongRevisions</c>).</summary>
public enum SongSaveReason { Save, Import, Restore }

/// <summary>
/// Guardar una canción. <c>Id</c> nulo = crear (sin <c>ExpectedRevision</c>); con <c>Id</c>, <c>ExpectedRevision</c> es obligatorio y debe ser la revisión vigente: si no, falla con
/// <c>song.conflict</c> sin cambiar nada. <c>Arrangement</c> son índices en <c>Sections</c>.
/// </summary>
public sealed record SongSaveRequest(
    long? Id, int? ExpectedRevision, string Title, string? Author, string? Copyright, string? CcliNumber, string? Notes, SongBackground? Background,
    IReadOnlyList<SongSectionInput> Sections, IReadOnlyList<int> Arrangement, SongSaveReason Reason = SongSaveReason.Save);

public sealed record SongSaveResult(long SongId, int Revision, bool Created);

public sealed record SongRevisionInfo(long Id, DateTimeOffset CreatedAt, string Reason, int SectionCount);

public sealed record SongDeleteResult(long SongId, int UsedInServices);
