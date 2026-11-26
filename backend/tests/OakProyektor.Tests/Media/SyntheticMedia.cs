using System.Buffers.Binary;
using System.Text;

namespace OakProyektor.Tests.Media;

/// <summary>
/// Archivos de medios SINTÉTICOS (solo cabeceras válidas, sin imagen ni sonido reales) generados por código: nada de terceros en el repositorio. Sirven para probar la sonda, la importación
/// y la biblioteca. No se pueden reproducir: la reproducción real se prueba en la app con material generado por <c>MediaRecorder</c> dentro de la propia salida.
/// </summary>
internal static class SyntheticMedia
{
    private static byte[] U32(long v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v); return b; }
    private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    public static byte[] Box(string type, params byte[][] payload)
    {
        var body = Cat(payload);
        return Cat(U32(8 + body.Length), Ascii(type), body);
    }

    public static byte[] Mp4(string videoFourCc = "avc1", string? audioFourCc = "mp4a", int width = 1920, int height = 1080, int durationMs = 10_000, bool moovAtEnd = false, int mdatBytes = 2048)
    {
        var mvhd = Box("mvhd", U32(0), U32(0), U32(0), U32(1000), U32(durationMs), new byte[80]);
        var traks = new List<byte[]>();
        if (videoFourCc.Length > 0) traks.Add(Trak("vide", videoFourCc, width, height));
        if (audioFourCc is not null) traks.Add(Trak("soun", audioFourCc, 0, 0));
        var moov = Box("moov", [mvhd, .. traks]);
        var ftyp = Box("ftyp", Ascii("isom"), U32(512), Ascii("isomiso2avc1mp41"));
        var mdat = Box("mdat", new byte[mdatBytes]);
        return moovAtEnd ? Cat(ftyp, mdat, moov) : Cat(ftyp, moov, mdat);
    }

    private static byte[] Trak(string handler, string fourcc, int width, int height)
    {
        var tkhd = Box("tkhd", U32(0), U32(0), U32(0), U32(1), U32(0), U32(0), new byte[8], new byte[8], new byte[36], U32((long)width << 16), U32((long)height << 16));
        var hdlr = Box("hdlr", U32(0), U32(0), Ascii(handler), new byte[12], new byte[1]);
        var entry = Cat(U32(86), Ascii(fourcc), new byte[78]);
        var stsd = Box("stsd", U32(0), U32(1), entry);
        return Box("trak", tkhd, Box("mdia", hdlr, Box("minf", Box("stbl", stsd))));
    }

    // ── WebM (EBML) ──
    private static byte[] Ebml(byte[] id, byte[] payload) => Cat(id, U32(0x10000000L | payload.Length), payload);
    private static byte[] EbmlUInt(byte[] id, long v)
    {
        var bytes = new List<byte>();
        do { bytes.Insert(0, (byte)(v & 0xFF)); v >>= 8; } while (v > 0);   // entero sin signo mínimo, big-endian
        return Ebml(id, [.. bytes]);
    }
    private static byte[] EbmlStr(byte[] id, string s) => Ebml(id, Ascii(s));

    public static byte[] Webm(string? videoCodec = "V_VP9", string? audioCodec = "A_OPUS", int width = 1280, int height = 720, double durationMs = 5000)
    {
        var header = Ebml([0x1A, 0x45, 0xDF, 0xA3], EbmlStr([0x42, 0x82], "webm"));
        var dur = new byte[8]; BinaryPrimitives.WriteDoubleBigEndian(dur, durationMs);
        var info = Ebml([0x15, 0x49, 0xA9, 0x66], Cat(EbmlUInt([0x2A, 0xD7, 0xB1], 1_000_000), Ebml([0x44, 0x89], dur)));
        var entries = new List<byte[]>();
        if (videoCodec is not null)
            entries.Add(Ebml([0xAE], Cat(EbmlUInt([0x83], 1), EbmlStr([0x86], videoCodec), Ebml([0xE0], Cat(EbmlUInt([0xB0], width), EbmlUInt([0xBA], height))))));
        if (audioCodec is not null) entries.Add(Ebml([0xAE], Cat(EbmlUInt([0x83], 2), EbmlStr([0x86], audioCodec))));
        var tracks = Ebml([0x16, 0x54, 0xAE, 0x6B], Cat([.. entries]));
        var cluster = Ebml([0x1F, 0x43, 0xB6, 0x75], new byte[64]);
        return Cat(header, Ebml([0x18, 0x53, 0x80, 0x67], Cat(info, tracks, cluster)));
    }

    // ── audio ──
    /// <summary>MP3 MPEG-1 Layer III, 128 kb/s, 44,1 kHz, estéreo (fotogramas de 417 bytes). Con <paramref name="xingFrames"/> lleva la cabecera Xing y la duración es EXACTA; sin ella se estima.</summary>
    public static byte[] Mp3(int? xingFrames = 1000, bool id3 = false, int plainFrames = 20)
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        if (xingFrames is { } f) { Ascii("Xing").CopyTo(frame, 36); frame[43] = 1; BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(44), (uint)f); }
        var tag = id3 ? Cat(Ascii("ID3"), [4, 0, 0], [0, 0, 0, 20], new byte[20]) : [];
        var frames = xingFrames is null ? Enumerable.Range(0, plainFrames).Select(_ => (byte[])frame.Clone()).ToArray() : [frame];
        return Cat(tag, Cat(frames));
    }

    public static byte[] Wav(int sampleRate = 44100, int channels = 2, double seconds = 2, int formatTag = 1)
    {
        var byteRate = sampleRate * channels * 2;
        var data = new byte[(int)(byteRate * seconds)];
        byte[] Le16(int v) => [(byte)v, (byte)(v >> 8)];
        byte[] Le32(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
        var fmt = Cat(Le16(formatTag), Le16(channels), Le32(sampleRate), Le32(byteRate), Le16(channels * 2), Le16(16));
        return Cat(Ascii("RIFF"), Le32(4 + 8 + 16 + 8 + data.Length), Ascii("WAVE"), Ascii("fmt "), Le32(16), fmt, Ascii("data"), Le32(data.Length), data);
    }

    public static byte[] Flac(int sampleRate = 44100, long totalSamples = 44100 * 3)
    {
        var info = new byte[34];
        info[10] = (byte)(sampleRate >> 12); info[11] = (byte)(sampleRate >> 4); info[12] = (byte)(((sampleRate & 0xF) << 4) | (1 << 1));
        info[13] = (byte)(0xF0 | (int)((totalSamples >> 32) & 0xF));
        BinaryPrimitives.WriteUInt32BigEndian(info.AsSpan(14), (uint)(totalSamples & 0xFFFFFFFF));
        return Cat(Ascii("fLaC"), [0x00, 0x00, 0x00, 0x22], info, new byte[64]);
    }

    public static byte[] Aac() => Cat([0xFF, 0xF1, 0x50, 0x80], new byte[512]);

    // ── imágenes ──
    public static byte[] Png(int width = 1920, int height = 1080) =>
        Cat([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], U32(13), Ascii("IHDR"), U32(width), U32(height), [8, 6, 0, 0, 0], new byte[16]);

    public static byte[] Jpeg(int width = 640, int height = 360)
    {
        byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];
        return Cat([0xFF, 0xD8], [0xFF, 0xE0], Be16(16), Ascii("JFIF\0"), [1, 1, 0], Be16(1), Be16(1), [0, 0], [0xFF, 0xC0], Be16(17), [8], Be16(height), Be16(width), [3, 1, 0x22, 0, 2, 0x11, 1, 3, 0x11, 1], [0xFF, 0xD9]);
    }

    public static byte[] WebpExtended(int width = 800, int height = 600)
    {
        byte[] Le24(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16)];
        var vp8x = Cat([0, 0, 0, 0], Le24(width - 1), Le24(height - 1));
        return Cat(Ascii("RIFF"), [0x1A, 0, 0, 0], Ascii("WEBP"), Ascii("VP8X"), [10, 0, 0, 0], vp8x);
    }
}
