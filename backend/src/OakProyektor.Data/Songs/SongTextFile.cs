using OakProyektor.Core.Songs;

namespace OakProyektor.Data.Songs;

/// <summary>
/// Lee un archivo .txt de una canción: tamaño acotado (<see cref="SongLimits.MaxFileBytes"/>) y decodificación UTF-8 estricta con respaldo a Windows-1252
/// (<see cref="SongTextDecoder"/>), indicando qué se detectó. Lo que no es texto (documentos de Office, PDF, imágenes) se rechaza con un mensaje claro.
/// Los errores (<see cref="SongException"/>) no citan el contenido.
/// </summary>
public static class SongTextFile
{
    public static async Task<DecodedSongText> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new SongException(SongErrorCodes.FileMissing, "No se encontró el archivo. Escribe la ruta completa de un archivo .txt.");

        byte[] bytes;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > SongLimits.MaxFileBytes) throw TooLarge(stream.Length);
            // Se lee como mucho un byte más que el límite: un archivo que crece mientras se lee (o un dispositivo sin tamaño) no puede agotar la memoria.
            var buffer = new byte[(int)Math.Min(stream.Length, SongLimits.MaxFileBytes) + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                total += read;
            }
            if (total > SongLimits.MaxFileBytes) throw TooLarge(total);
            bytes = buffer.AsSpan(0, total).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SongException(SongErrorCodes.FileMissing, "No se pudo leer el archivo (¿está abierto en otro programa o no tienes permiso?).", ex);
        }
        return SongTextDecoder.Decode(bytes);
    }

    private static SongException TooLarge(long bytes) =>
        new(SongErrorCodes.FileTooLarge, $"El archivo es demasiado grande para ser la letra de una canción ({bytes / 1024:N0} KB; el máximo es {SongLimits.MaxFileBytes / 1024} KB).");
}
