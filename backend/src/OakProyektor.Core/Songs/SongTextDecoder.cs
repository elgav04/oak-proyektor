using System.Text;
using System.Text.RegularExpressions;

namespace OakProyektor.Core.Songs;

/// <summary>Texto leído de un archivo y QUÉ se detectó (se muestra en la vista previa; la lección de los archivos de la Biblia con mojibake).</summary>
/// <param name="Encoding">Codificación aplicada: <c>utf-8</c>, <c>utf-8-bom</c>, <c>utf-16le</c>, <c>utf-16be</c> o <c>windows-1252</c>.</param>
/// <param name="FellBack">true si el archivo NO era UTF-8 válido y se interpretó como Windows-1252.</param>
public sealed record DecodedSongText(string Text, string Encoding, bool FellBack, IReadOnlyList<string> Warnings);

/// <summary>
/// Decodifica los bytes de un archivo de texto: UTF-8 ESTRICTO (con o sin BOM) y, si falla, Windows-1252 (indicándolo); UTF-16 solo con BOM. Rechaza con un mensaje claro
/// lo que no es texto (documentos de Office, PDF, imágenes, binarios) y lo demasiado grande. Función pura; no usa proveedores de codificación globales
/// (la tabla de Windows-1252 está aquí).
/// </summary>
public static partial class SongTextDecoder
{
    public static DecodedSongText Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > SongLimits.MaxFileBytes)
            throw new SongException(SongErrorCodes.FileTooLarge, $"El archivo es demasiado grande para ser la letra de una canción ({bytes.Length / 1024:N0} KB; el máximo es {SongLimits.MaxFileBytes / 1024} KB).");
        if (bytes.Length == 0) return new DecodedSongText("", "utf-8", false, []);

        if (NotTextReason(bytes) is { } notText) throw new SongException(SongErrorCodes.NotText, notText);

        string text;
        string encoding;
        var fellBack = false;
        var warnings = new List<string>();

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { text = DecodeUtf16(bytes[2..], bigEndian: false); encoding = "utf-16le"; }
        else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { text = DecodeUtf16(bytes[2..], bigEndian: true); encoding = "utf-16be"; }
        else
        {
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var body = hasBom ? bytes[3..] : bytes;
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(body);
                encoding = hasBom ? "utf-8-bom" : "utf-8";
            }
            catch (DecoderFallbackException)
            {
                text = DecodeWindows1252(body, out var undefined);
                encoding = "windows-1252";
                fellBack = true;
                warnings.Add("El archivo no está en UTF-8: se interpretó como Windows-1252. Revisa que los acentos y la «ñ» se vean bien en la vista previa.");
                if (undefined > 0) warnings.Add($"{undefined} byte(s) no tienen equivalente en Windows-1252 y se sustituyeron por «�».");
            }
        }

        if (text.Contains('�')) warnings.Add("El texto contiene caracteres «�»: parte del archivo no se pudo leer bien.");
        if (LooksMojibake(text)) warnings.Add("Los acentos parecen dañados (por ejemplo «Ã©» en vez de «é»): el archivo se guardó con una codificación equivocada. Revisa la vista previa.");
        return new DecodedSongText(text, encoding, fellBack, warnings);
    }

    /// <summary>Secuencias típicas de un texto UTF-8 leído como Windows-1252 («Ã©», «Ã±», «Â¿», «â€™»).</summary>
    public static bool LooksMojibake(string text) => Mojibake().IsMatch(text);

    // ───────────────────────────── internos ─────────────────────────────

    private static string? NotTextReason(ReadOnlySpan<byte> b)
    {
        if (StartsWith(b, "PK\u0003\u0004"u8)) return "El archivo parece un documento de Word o PowerPoint (o un .zip), no un archivo de texto. Guárdalo como «Texto sin formato (.txt)» y vuelve a intentarlo.";
        if (StartsWith(b, "%PDF"u8)) return "El archivo es un PDF, no un archivo de texto.";
        if (StartsWith(b, "{\\rtf"u8)) return "El archivo es un RTF (texto con formato), no un archivo de texto sin formato. Guárdalo como .txt.";
        if (StartsWith(b, [0xD0, 0xCF, 0x11, 0xE0])) return "El archivo parece un documento antiguo de Office (.doc/.ppt), no un archivo de texto. Guárdalo como .txt.";
        if (StartsWith(b, [0x89, 0x50, 0x4E, 0x47]) || StartsWith(b, [0xFF, 0xD8, 0xFF]) || StartsWith(b, "GIF8"u8)) return "El archivo es una imagen, no un archivo de texto.";
        if (b.Length >= 2 && ((b[0] == 0xFF && b[1] == 0xFE) || (b[0] == 0xFE && b[1] == 0xFF))) return null;   // UTF-16 con BOM: los ceros son normales

        var controls = 0;
        foreach (var x in b)
        {
            if (x == 0) return "El archivo contiene bytes nulos: parece un archivo binario, no un archivo de texto.";
            if (x < 0x20 && x is not (0x09 or 0x0A or 0x0D or 0x0C)) controls++;
        }
        if (controls > 8 && controls * 50 > b.Length) return "El archivo contiene muchos caracteres de control: parece un archivo binario, no un archivo de texto.";
        return null;
    }

    private static bool StartsWith(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix) => bytes.StartsWith(prefix);

    private static string DecodeUtf16(ReadOnlySpan<byte> body, bool bigEndian)
    {
        try { return new UnicodeEncoding(bigEndian, byteOrderMark: false, throwOnInvalidBytes: true).GetString(body); }
        catch (DecoderFallbackException) { throw new SongException(SongErrorCodes.NotText, "El archivo dice estar en UTF-16 pero su contenido no es texto válido."); }
    }

    /// <summary>Los 32 bytes 0x80–0x9F de Windows-1252 (0 = sin equivalente: 0x81, 0x8D, 0x8F, 0x90, 0x9D). El resto coincide con Latin-1.</summary>
    private static readonly char[] High =
    [
        '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡', 'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
        '\0', '‘', '’', '“', '”', '•', '–', '—', '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ',
    ];

    private static string DecodeWindows1252(ReadOnlySpan<byte> bytes, out int undefined)
    {
        undefined = 0;
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var x = bytes[i];
            if (x is >= 0x80 and <= 0x9F)
            {
                var mapped = High[x - 0x80];
                if (mapped == '\0') { chars[i] = '�'; undefined++; }
                else chars[i] = mapped;
            }
            else chars[i] = (char)x;
        }
        return new string(chars);
    }

    [GeneratedRegex("Ã[ -¿ŒœŠšŸŽžƒˆ˜–-›€™]|Â[ -¿]|â€")]
    private static partial Regex Mojibake();
}
