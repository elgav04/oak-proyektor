namespace OakProyektor.App;

internal static class WritableDirectory
{
    /// <summary>
    /// Crea la carpeta (si hace falta) y comprueba que se puede ESCRIBIR en ella con un archivo de prueba. Lanza si no.
    /// Hace falta comprobarlo de verdad: ni Serilog ni WebView2 avisan si no pueden escribir; fallan más tarde y en silencio.
    /// </summary>
    public static void Ensure(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("carpeta vacía");
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".escritura-{Environment.ProcessId}.tmp");
        using (new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
    }

    /// <summary>La primera carpeta de <paramref name="candidates"/> donde se puede escribir; null si ninguna. <paramref name="failures"/> recoge los motivos.</summary>
    public static string? FirstWritable(IEnumerable<string> candidates, List<string> failures)
    {
        foreach (var candidate in candidates)
        {
            try { Ensure(candidate); return candidate; }
            catch (Exception ex) { failures.Add($"{candidate}: {ex.Message}"); }
        }
        return null;
    }
}