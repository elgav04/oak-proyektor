namespace OakProyektor.App;

/// <summary>
/// El perfil de WebView2 EFECTIVO que usan las dos ventanas (siempre el mismo: comparten el entorno, ver ApplicationHost, hallazgo I8).
/// Es una caché regenerable y la interfaz es prioritaria: si la carpeta de datos no se puede escribir, el perfil cae a una carpeta
/// temporal y, si tampoco, al predeterminado de Photino. Sin este respaldo, una carpeta de datos imposible dejaba el panel en NEGRO
/// (WebView2 no avisa: no puede crear su perfil y no dibuja nada), así que el operador ni siquiera veía el aviso de la base de datos.
/// </summary>
/// <param name="Directory">Carpeta a usar, o null para el predeterminado de Photino (<c>%LOCALAPPDATA%\Photino</c>).</param>
/// <param name="Note">Por qué no se usó la carpeta preferida (para el log); null si se usó.</param>
internal sealed record WebViewProfile(string? Directory, string? Note)
{
    public static WebViewProfile Choose(ApplicationPaths paths, string temporaryRoot)
    {
        var failures = new List<string>();
        var temporary = Path.Combine(temporaryRoot, ApplicationPaths.ProductFolderName, "WebView2");
        var chosen = WritableDirectory.FirstWritable([paths.WebViewDataDirectory, temporary], failures);

        if (chosen == paths.WebViewDataDirectory) return new WebViewProfile(chosen, null);
        var why = string.Join(" | ", failures);
        return chosen is null
            ? new WebViewProfile(null, $"No se pudo escribir el perfil de WebView2 en ninguna carpeta ({why}); se usa el predeterminado de Photino.")
            : new WebViewProfile(chosen, $"No se pudo escribir el perfil de WebView2 en la carpeta de datos ({why}); se usa {chosen}.");
    }

    /// <summary>Aplica el perfil a una ventana (si hay carpeta). Se aplica IGUAL a todas las ventanas.</summary>
    public Photino.NET.PhotinoWindow ApplyTo(Photino.NET.PhotinoWindow window) =>
        Directory is null ? window : window.SetTemporaryFilesPath(Directory);
}