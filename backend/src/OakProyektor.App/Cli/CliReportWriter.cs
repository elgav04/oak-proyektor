using System.Globalization;
using OakProyektor.Data.Scripture;

namespace OakProyektor.App.Cli;

/// <summary>
/// Escribe los informes de las órdenes de consola. SOLO conteos y referencias (códigos USFM): ningún texto bíblico, ni siquiera el de los versículos que
/// difieren. (La orden <c>--show-verse</c> es otra cosa: muestra el texto a propósito y lo escribe <c>CliRunner</c>.)
/// </summary>
internal static class CliReportWriter
{
    private static string N(long value) => value.ToString("N0", CultureInfo.GetCultureInfo("es"));

    public static void Write(ImportReport r, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine($"== Informe de importación ==");
        w.WriteLine($"Biblia id {r.BibleId} ({(r.CreatedBible ? "creada" : "actualizada en el sitio")}); formato {r.FormatId}; perfil de numeración {r.NumberingProfile}");
        w.WriteLine($"Perfil de numeración aplicado: {r.NumberingProfile}{(r.NumberingProfileKept ? " (conservado: no se indicó --numbering)" : "")}");
        w.WriteLine($"Versículos de la fuente (tras el perfil): {N(r.SourceVerses)}");
        w.WriteLine($"  nuevos {N(r.Inserted)}, actualizados {N(r.Updated)}, sin cambios {N(r.Unchanged)}, eliminados {N(r.Deleted)} (libros eliminados {N(r.BooksDeleted)})");
        if (!r.CreatedBible)
        {
            var percent = r.PreviousVerses > 0 ? 100.0 * r.Updated / r.PreviousVerses : 0;
            w.WriteLine($"Textos modificados: {N(r.Updated)} de los {N(r.PreviousVerses)} que había ({percent:0.##} %)");
        }
        w.WriteLine(r.BackupPath is null ? "Copia de seguridad previa: no hizo falta (no se modificó ni borró nada)" : $"Copia de seguridad previa: {r.BackupPath}");
        if (r.DiscardedVerses > 0) w.WriteLine($"Versículos descartados (numeración inválida o fuera del canon): {N(r.DiscardedVerses)}");
        w.WriteLine($"Limpieza: {N(r.CleanedVerses)} versículos limpiados; {N(r.JoinedVerses)} unidos desde varios trozos; {N(r.DuplicateChaptersDiscarded)} capítulos repetidos descartados{(r.DuplicateChaptersDifferent > 0 ? $" (¡{N(r.DuplicateChaptersDifferent)} con texto DISTINTO!)" : "")}");
        if (r.MergesApplied.Count > 0) w.WriteLine($"Uniones de numeración aplicadas: {string.Join(", ", r.MergesApplied)}");
        if (r.MergesSkipped.Count > 0) w.WriteLine($"Uniones de numeración NO aplicadas: {string.Join(", ", r.MergesSkipped)}");
        if (r.AffectedItems.Count > 0)
        {
            w.WriteLine($"ATENCIÓN: {r.AffectedItems.Count} elemento(s) apuntaban a versículos eliminados:");
            foreach (var item in r.AffectedItems)
                w.WriteLine($"  - {item.Kind} #{item.Id}{(item.PlaylistId is { } p ? $" (servicio {p})" : "")}: {item.Reference}");
        }
        WriteValidation(r.Validation, w);
        w.WriteLine($"Índice de búsqueda (FTS integrity-check): {(r.FtsIntegrityOk ? "correcto" : "FALLÓ")}{(r.FtsNote is null ? "" : " — " + r.FtsNote)}");
        if (r.Warnings.Count > 0)
        {
            w.WriteLine($"Avisos ({r.Warnings.Count}):");
            foreach (var warning in r.Warnings) w.WriteLine("  - " + warning);
        }
        w.WriteLine($"Tiempo: {N(r.ElapsedMs)} ms");
    }

    public static void WriteValidation(ValidationReport v, TextWriter w)
    {
        w.WriteLine($"Validación: {v.Books} libros, {N(v.Chapters)} capítulos, {N(v.Verses)} versículos, {N(v.EmptyVerses)} vacíos");
        if (v.MissingBooks.Count > 0) w.WriteLine($"  libros que faltan: {string.Join(", ", v.MissingBooks)}");
        foreach (var mismatch in v.ChapterCountMismatches) w.WriteLine("  capítulos: " + mismatch);
        if (v.VerseGapChapters > 0)
        {
            w.WriteLine($"  capítulos con huecos o que no empiezan en 1: {v.VerseGapChapters}{(v.VerseGaps.Count < v.VerseGapChapters ? $" (se listan {v.VerseGaps.Count})" : "")}");
            foreach (var gap in v.VerseGaps) w.WriteLine("    " + gap);
        }
        w.WriteLine($"  diferencia con el total habitual (31 102): {v.DifferenceFromStandardVerseTotal:+#;-#;0}");
        foreach (var warning in v.Warnings) w.WriteLine("  aviso: " + warning);
    }

    public static void Write(CrossCheckReport r, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("== Verificación cruzada ==");
        w.WriteLine($"Archivo en formato {r.FormatId}; perfil de numeración aplicado al archivo: {r.NumberingProfile}");
        w.WriteLine($"En la BD: {N(r.InDatabase)}; en el archivo: {N(r.InFile)}");
        w.WriteLine($"  idénticos: {N(r.Identical)}");
        w.WriteLine($"  (a) solo espacios o puntuación: {N(r.PunctuationOrSpacingOnly)}");
        w.WriteLine($"  (b) solo tildes o mayúsculas: {N(r.AccentOrCaseOnly)}");
        w.WriteLine($"  (c) palabras distintas: {N(r.WordsDiffer)}");
        w.WriteLine($"  Salmo 119 con una palabra de más al final (rótulo de estrofa): {N(r.KnownLabelVerses)}");
        w.WriteLine($"  solo en la BD: {N(r.OnlyInDatabase)}; solo en el archivo: {N(r.OnlyInFile)}");
        WriteRefs(w, "(b) tildes o mayúsculas", r.AccentOrCaseRefs, r.AccentOrCaseOnly);
        WriteRefs(w, "(c) palabras distintas", r.WordsDifferRefs, r.WordsDiffer);
        WriteRefs(w, "rótulos del Salmo 119", r.KnownLabelRefs, r.KnownLabelVerses);
        WriteRefs(w, "solo en la BD", r.OnlyInDatabaseRefs, r.OnlyInDatabase);
        WriteRefs(w, "solo en el archivo", r.OnlyInFileRefs, r.OnlyInFile);
        if (r.Warnings.Count > 0)
        {
            w.WriteLine($"Avisos ({r.Warnings.Count}):");
            foreach (var warning in r.Warnings) w.WriteLine("  - " + warning);
        }
        else w.WriteLine("Sin avisos: el resultado es el esperado.");
    }

    private static void WriteRefs(TextWriter w, string title, IReadOnlyList<string> refs, int total)
    {
        if (refs.Count == 0) return;
        w.WriteLine($"  Referencias — {title}{(refs.Count < total ? $" (primeras {refs.Count} de {N(total)})" : "")}:");
        w.WriteLine("    " + string.Join("; ", refs));
    }
}
