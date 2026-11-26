using System.Buffers.Binary;
using System.Text;

namespace OakProyektor.Core.Media;

/// <summary>Lo que se averigua de las CABECERAS de un archivo (sin decodificar). <c>Container</c> nulo = no se reconoció. <c>DurationApproximate</c>: se estimó (MP3 sin cabecera Xing).</summary>
public sealed record MediaProbeResult(
    string? Container, string? VideoCodec, string? AudioCodec, long? DurationMs, int? Width, int? Height, bool HasAudio, bool DurationApproximate)
{
    public static MediaProbeResult Unknown { get; } = new(null, null, null, null, null, null, false, false);
}

/// <summary>Veredicto de un archivo a partir de su sonda: qué se hace con él al importar.</summary>
/// <param name="RejectCode">Si no es nulo, se RECHAZA la importación (no se guarda nada) con ese código.</param>
/// <param name="StatusCode">Aviso (<c>media.videoCodecWarning</c>…) que se guarda con el elemento; nulo = nada que avisar.</param>
public sealed record MediaVerdict(string? RejectCode, string? StatusCode);

/// <summary>
/// Sonda de cabeceras en C# puro, ACOTADA y sin dependencias (no hay <c>ffmpeg</c>): MP4/M4V/M4A, WebM, MP3, WAV, FLAC, AAC (ADTS) e imágenes PNG/JPEG/WebP. Lee como mucho 1 MiB de
/// cabecera más átomos <c>moov</c> de hasta 32 MiB (al principio o al final del archivo), con profundidad ≤ 6 y un máximo de pasos. Una entrada hostil o truncada devuelve «desconocido»:
/// NUNCA lanza ni excede el presupuesto. La verificación que manda es la del navegador (carga y primer fotograma); esto da códec y mensajes claros sin decodificar.
/// </summary>
public static class MediaProbe
{
    private const int MaxMoovBytes = 32 * 1024 * 1024;
    private const int MaxSteps = 20_000;
    private const int MaxDepth = 6;

    public static MediaProbeResult Probe(Stream stream, string ext)
    {
        try
        {
            if (!stream.CanSeek) return MediaProbeResult.Unknown;
            stream.Position = 0;
            return ext.ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" or ".m4a" => ProbeMp4(stream),
                ".webm" => ProbeWebm(stream),
                ".mp3" => ProbeMp3(stream),
                ".wav" => ProbeWav(stream),
                ".flac" => ProbeFlac(stream),
                ".aac" => ProbeAdts(stream),
                ".png" => ProbePng(stream),
                ".jpg" or ".jpeg" => ProbeJpeg(stream),
                ".webp" => ProbeWebp(stream),
                _ => MediaProbeResult.Unknown,
            };
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException or InvalidDataException or OverflowException or NotSupportedException or UnauthorizedAccessException)
        {
            return MediaProbeResult.Unknown;
        }
    }

    // ─────────────────────────── veredicto ───────────────────────────

    private static readonly HashSet<string> SupportedVideo = ["H.264", "VP8", "VP9", "AV1", "HEVC"];
    private static readonly HashSet<string> SupportedAudio = ["AAC", "MP3", "Opus", "Vorbis", "FLAC", "PCM"];

    /// <summary>Qué hacer con un archivo según su sonda (HEVC se ACEPTA con aviso: decisión aprobada; AC-3/E-AC-3 avisa; lo desconocido o no soportado rechaza).</summary>
    public static MediaVerdict Judge(MediaItemKind kind, MediaProbeResult p)
    {
        if (p.Container is null) return new MediaVerdict(MediaErrorCodes.UnsupportedContainer, null);
        switch (kind)
        {
            case MediaItemKind.Image:
                return p.Width is > 0 && p.Height is > 0 ? new MediaVerdict(null, null) : new MediaVerdict(MediaErrorCodes.UnsupportedContainer, null);
            case MediaItemKind.Audio:
                if (p.AudioCodec is null || !SupportedAudio.Contains(p.AudioCodec)) return new MediaVerdict(MediaErrorCodes.AudioCodecUnsupported, null);
                return new MediaVerdict(null, null);
            default:
                if (p.VideoCodec is null || !SupportedVideo.Contains(p.VideoCodec)) return new MediaVerdict(MediaErrorCodes.VideoCodecUnsupported, null);
                if (p.VideoCodec == "HEVC") return new MediaVerdict(null, MediaErrorCodes.VideoCodecWarning);
                if (p.AudioCodec is "AC-3" or "E-AC-3") return new MediaVerdict(null, MediaErrorCodes.AudioCodecWarning);
                // Un audio que no se reproduce no impide usar el video (los fondos van en silencio): se avisa.
                if (p.HasAudio && (p.AudioCodec is null || !SupportedAudio.Contains(p.AudioCodec))) return new MediaVerdict(null, MediaErrorCodes.AudioCodecUnsupported);
                return new MediaVerdict(null, null);
        }
    }

    // ─────────────────────────── MP4 / M4A ───────────────────────────

    private sealed record Box(string Type, long Start, long PayloadStart, long End);

    private static MediaProbeResult ProbeMp4(Stream s)
    {
        var header = new byte[16];
        if (ReadAt(s, 0, header) < 12 || Encoding.ASCII.GetString(header, 4, 4) != "ftyp") return MediaProbeResult.Unknown;

        // Cajas de primer nivel: se SALTA el contenido (mdat puede pesar gigas); solo se lee `moov`.
        long pos = 0; var steps = 0;
        while (pos + 8 <= s.Length && steps++ < 256)
        {
            var box = ReadBox(s, pos, s.Length);
            if (box is null) break;
            if (box.Type == "moov")
            {
                var size = box.End - box.PayloadStart;
                if (size <= 0 || size > MaxMoovBytes) return new MediaProbeResult("MP4", null, null, null, null, null, false, false);
                var data = new byte[size];
                if (ReadAt(s, box.PayloadStart, data) != size) return new MediaProbeResult("MP4", null, null, null, null, null, false, false);
                return ParseMoov(data);
            }
            if (box.End <= pos) break;
            pos = box.End;
        }
        return new MediaProbeResult("MP4", null, null, null, null, null, false, false);   // sin moov (fragmentado o truncado): reconocido pero sin datos
    }

    private static Box? ReadBox(Stream s, long pos, long limit)
    {
        var buf = new byte[16];
        var n = ReadAt(s, pos, buf);
        if (n < 8) return null;
        long size = BinaryPrimitives.ReadUInt32BigEndian(buf);
        var type = Encoding.ASCII.GetString(buf, 4, 4);
        long payload = pos + 8;
        if (size == 1) { if (n < 16) return null; size = (long)BinaryPrimitives.ReadUInt64BigEndian(buf.AsSpan(8)); payload = pos + 16; }
        else if (size == 0) size = limit - pos;
        if (size < payload - pos || pos + size > limit) return null;
        return new Box(type, pos, payload, pos + size);
    }

    private sealed class Mp4Acc
    {
        public long Timescale, Duration; public int Width, Height; public string? Video, Audio; public bool HasVideo, HasAudio;
    }

    private static MediaProbeResult ParseMoov(byte[] d)
    {
        var acc = new Mp4Acc();
        var steps = 0;
        ParseBoxes(d, 0, d.Length, 0, ref steps, acc, "moov", null);
        long? ms = acc.Timescale > 0 && acc.Duration > 0 ? acc.Duration * 1000 / acc.Timescale : null;
        return new MediaProbeResult("MP4", acc.Video, acc.Audio, ms, acc.Width > 0 ? acc.Width : null, acc.Height > 0 ? acc.Height : null, acc.HasAudio, false);
    }

    private static void ParseBoxes(byte[] d, int start, int end, int depth, ref int steps, Mp4Acc acc, string parent, string? handler)
    {
        if (depth > MaxDepth) return;
        var pos = start;
        while (pos + 8 <= end && steps++ < MaxSteps)
        {
            long size = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(pos));
            var type = Encoding.ASCII.GetString(d, pos + 4, 4);
            var payload = pos + 8;
            if (size == 1) { if (pos + 16 > end) return; size = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(pos + 8)); payload = pos + 16; }
            else if (size == 0) size = end - pos;
            if (size < payload - pos || pos + size > end) return;
            var boxEnd = (int)(pos + size);
            switch (type)
            {
                case "mvhd" when parent == "moov": ReadMvhd(d, payload, boxEnd, acc); break;
                case "trak": { var t = new Mp4Acc(); ParseBoxes(d, payload, boxEnd, depth + 1, ref steps, t, "trak", null); Merge(acc, t); break; }
                case "tkhd": ReadTkhd(d, payload, boxEnd, acc); break;
                case "mdia": case "minf": case "stbl": ParseBoxes(d, payload, boxEnd, depth + 1, ref steps, acc, type, handler); break;
                case "hdlr": { var h = ReadHdlr(d, payload, boxEnd); if (h == "vide") acc.HasVideo = true; if (h == "soun") acc.HasAudio = true; break; }
                case "stsd": { var codec = ReadStsd(d, payload, boxEnd); if (codec is not null) AssignCodec(acc, codec); break; }
            }
            pos = boxEnd;
        }
    }

    private static void AssignCodec(Mp4Acc acc, string fourcc)
    {
        var name = fourcc switch
        {
            "avc1" or "avc3" => "H.264", "hvc1" or "hev1" => "HEVC", "av01" => "AV1", "vp09" => "VP9", "vp08" => "VP8",
            "mp4a" => "AAC", "ac-3" => "AC-3", "ec-3" => "E-AC-3", "alac" => "ALAC", "Opus" => "Opus", "fLaC" => "FLAC", "mp4v" => "MPEG-4", "apch" or "apcn" or "apcs" or "apco" => "ProRes",
            _ => fourcc,
        };
        if (fourcc is "mp4a" or "ac-3" or "ec-3" or "alac" or "Opus" or "fLaC") acc.Audio ??= name; else acc.Video ??= name;
    }

    private static void Merge(Mp4Acc into, Mp4Acc t)
    {
        if (t.HasVideo) { into.HasVideo = true; if (t.Width > 0 && into.Width == 0) { into.Width = t.Width; into.Height = t.Height; } }
        if (t.HasAudio) into.HasAudio = true;
        into.Video ??= t.Video; into.Audio ??= t.Audio;
    }

    private static void ReadMvhd(byte[] d, int p, int end, Mp4Acc acc)
    {
        if (p + 20 > end) return;
        var version = d[p];
        if (version == 1) { if (p + 32 > end) return; acc.Timescale = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 20)); acc.Duration = (long)BinaryPrimitives.ReadUInt64BigEndian(d.AsSpan(p + 24)); }
        else { acc.Timescale = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 12)); acc.Duration = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 16)); }
    }

    private static void ReadTkhd(byte[] d, int p, int end, Mp4Acc acc)
    {
        var v = p < end ? d[p] : 0;
        var w = v == 1 ? p + 88 : p + 76;
        if (w + 8 > end) return;
        acc.Width = (int)(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(w)) >> 16);
        acc.Height = (int)(BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(w + 4)) >> 16);
    }

    private static string? ReadHdlr(byte[] d, int p, int end) => p + 12 <= end ? Encoding.ASCII.GetString(d, p + 8, 4) : null;

    private static string? ReadStsd(byte[] d, int p, int end) => p + 16 <= end && BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(p + 4)) > 0 ? Encoding.ASCII.GetString(d, p + 12, 4) : null;

    // ─────────────────────────── WebM (EBML) ───────────────────────────

    private static MediaProbeResult ProbeWebm(Stream s)
    {
        var head = new byte[4];
        if (ReadAt(s, 0, head) < 4 || head[0] != 0x1A || head[1] != 0x45 || head[2] != 0xDF || head[3] != 0xA3) return MediaProbeResult.Unknown;
        var limit = Math.Min(s.Length, 1024 * 1024);
        var d = new byte[limit];
        var n = ReadAt(s, 0, d);
        string? video = null, audio = null; long scale = 1_000_000; double duration = 0; int w = 0, h = 0; var steps = 0;
        ParseEbml(d, 0, n, 0, ref steps, ref video, ref audio, ref scale, ref duration, ref w, ref h);
        long? ms = duration > 0 ? (long)(duration * scale / 1_000_000.0) : null;
        return new MediaProbeResult("WebM", video, audio, ms, w > 0 ? w : null, h > 0 ? h : null, audio is not null, false);
    }

    private static void ParseEbml(byte[] d, int start, int end, int depth, ref int steps, ref string? video, ref string? audio, ref long scale, ref double duration, ref int w, ref int h)
    {
        if (depth > MaxDepth) return;
        var pos = start; var trackType = 0; string? codec = null;
        while (pos < end && steps++ < MaxSteps)
        {
            if (!ReadVint(d, pos, end, keepMarker: true, out var id, out var idLen)) return;
            if (!ReadVint(d, pos + idLen, end, keepMarker: false, out var size, out var sizeLen)) return;
            var dataStart = pos + idLen + sizeLen;
            var unknown = size == (1L << (7 * sizeLen)) - 1;
            var dataEnd = unknown ? end : (int)Math.Min(end, dataStart + size);
            if (dataStart > end) return;
            switch (id)
            {
                case 0x18538067 or 0x1549A966 or 0x1654AE6B or 0xAE or 0xE0:   // Segment, Info, Tracks, TrackEntry, Video: contenedores
                    ParseEbml(d, dataStart, dataEnd, depth + 1, ref steps, ref video, ref audio, ref scale, ref duration, ref w, ref h);
                    break;
                case 0x2AD7B1: scale = ReadUInt(d, dataStart, dataEnd); break;
                case 0x4489: duration = ReadFloat(d, dataStart, dataEnd); break;
                case 0x83: trackType = (int)ReadUInt(d, dataStart, dataEnd); break;
                case 0x86: codec = Encoding.ASCII.GetString(d, dataStart, Math.Max(0, dataEnd - dataStart)).TrimEnd('\0'); break;
                case 0xB0: w = (int)ReadUInt(d, dataStart, dataEnd); break;
                case 0xBA: h = (int)ReadUInt(d, dataStart, dataEnd); break;
                case 0x1F43B675: if (depth >= 1) return; break;   // Cluster: ya no hay más cabeceras
            }
            if (id == 0x1F43B675) return;
            if (unknown) return;
            pos = dataEnd;
        }
        if (codec is not null)
        {
            var name = codec switch { "V_VP8" => "VP8", "V_VP9" => "VP9", "V_AV1" => "AV1", "V_MPEG4/ISO/AVC" => "H.264", "V_MPEGH/ISO/HEVC" => "HEVC", "A_OPUS" => "Opus", "A_VORBIS" => "Vorbis", "A_AAC" => "AAC", "A_FLAC" => "FLAC", "A_AC3" => "AC-3", _ => codec };
            if (trackType == 2 || codec.StartsWith("A_", StringComparison.Ordinal)) audio ??= name; else video ??= name;
        }
    }

    private static bool ReadVint(byte[] d, int pos, int end, bool keepMarker, out long value, out int length)
    {
        value = 0; length = 0;
        if (pos >= end) return false;
        var first = d[pos];
        if (first == 0) return false;
        length = 1; var mask = 0x80;
        while ((first & mask) == 0) { mask >>= 1; length++; }
        if (length > 8 || pos + length > end) return false;
        value = keepMarker ? first : first & (mask - 1);
        for (var i = 1; i < length; i++) value = (value << 8) | d[pos + i];
        return true;
    }

    private static long ReadUInt(byte[] d, int p, int end) { long v = 0; for (var i = p; i < end && i < p + 8; i++) v = (v << 8) | d[i]; return v; }

    private static double ReadFloat(byte[] d, int p, int end)
    {
        if (end - p == 4) return BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(p));
        if (end - p == 8) return BinaryPrimitives.ReadDoubleBigEndian(d.AsSpan(p));
        return 0;
    }

    // ─────────────────────────── MP3 / WAV / FLAC / AAC ───────────────────────────

    private static readonly int[] Mpeg1L3Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
    private static readonly int[] Mpeg2L3Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
    private static readonly int[] Mpeg1Rates = [44100, 48000, 32000, 0];

    private static MediaProbeResult ProbeMp3(Stream s)
    {
        var buf = new byte[Math.Min(s.Length, 128 * 1024)];
        var n = ReadAt(s, 0, buf);
        var pos = 0;
        if (n >= 10 && buf[0] == 'I' && buf[1] == 'D' && buf[2] == '3')
            pos = 10 + ((buf[6] & 0x7F) << 21 | (buf[7] & 0x7F) << 14 | (buf[8] & 0x7F) << 7 | (buf[9] & 0x7F));
        var id3 = pos;
        if (pos > n - 4)
        {   // la cabecera ID3 es más grande que lo leído: se busca el primer fotograma tras ella
            var more = new byte[8192];
            if (ReadAt(s, pos, more) < 4) return MediaProbeResult.Unknown;
            buf = more; n = more.Length; pos = 0;
        }
        for (var i = pos; i + 4 <= n; i++)
        {
            if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;
            var versionBits = (buf[i + 1] >> 3) & 3; var layerBits = (buf[i + 1] >> 1) & 3;
            if (versionBits == 1 || layerBits != 1) continue;   // reservado o no es Layer III
            var bitrateIdx = buf[i + 2] >> 4; var rateIdx = (buf[i + 2] >> 2) & 3;
            if (bitrateIdx is 0 or 15 || rateIdx == 3) continue;
            var mpeg1 = versionBits == 3;
            var bitrate = (mpeg1 ? Mpeg1L3Bitrates : Mpeg2L3Bitrates)[bitrateIdx] * 1000;
            var rate = Mpeg1Rates[rateIdx] / (versionBits == 3 ? 1 : versionBits == 2 ? 2 : 4);
            var samplesPerFrame = mpeg1 ? 1152 : 576;
            var mono = ((buf[i + 3] >> 6) & 3) == 3;
            var sideInfo = mpeg1 ? (mono ? 17 : 32) : (mono ? 9 : 17);
            var tag = i + 4 + sideInfo;
            long? ms = null; var approx = false;
            if (tag + 12 <= n && (Encoding.ASCII.GetString(buf, tag, 4) is "Xing" or "Info") && (buf[tag + 7] & 1) != 0)
            {
                var frames = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(tag + 8));
                ms = (long)(frames * (double)samplesPerFrame / rate * 1000);
            }
            else if (bitrate > 0) { ms = (long)((s.Length - id3) * 8.0 / bitrate * 1000); approx = true; }
            return new MediaProbeResult("MP3", null, "MP3", ms, null, null, true, approx);
        }
        return MediaProbeResult.Unknown;
    }

    private static MediaProbeResult ProbeWav(Stream s)
    {
        var d = new byte[Math.Min(s.Length, 4096)];
        var n = ReadAt(s, 0, d);
        if (n < 12 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "WAVE") return MediaProbeResult.Unknown;
        var pos = 12; int tag = 0; long byteRate = 0; long dataSize = -1;
        for (var i = 0; i < 64 && pos + 8 <= n; i++)
        {
            var id = Encoding.ASCII.GetString(d, pos, 4); long size = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + 4));
            if (id == "fmt " && pos + 8 + 16 <= n) { tag = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(pos + 8)); byteRate = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(pos + 16)); }
            else if (id == "data") { dataSize = size; break; }
            pos += 8 + (int)Math.Min(size + (size & 1), int.MaxValue / 2);
        }
        var codec = tag is 1 or 3 or 0xFFFE ? "PCM" : $"WAV-0x{tag:X}";
        long? ms = byteRate > 0 && dataSize >= 0 ? Math.Min(dataSize, s.Length) * 1000 / byteRate : null;
        return new MediaProbeResult("WAV", null, codec, ms, null, null, true, false);
    }

    private static MediaProbeResult ProbeFlac(Stream s)
    {
        var d = new byte[42];
        if (ReadAt(s, 0, d) < 42 || Encoding.ASCII.GetString(d, 0, 4) != "fLaC" || (d[4] & 0x7F) != 0) return MediaProbeResult.Unknown;
        var rate = (d[18] << 12) | (d[19] << 4) | (d[20] >> 4);
        long total = ((long)(d[21] & 0x0F) << 32) | ((long)d[22] << 24) | ((long)d[23] << 16) | ((long)d[24] << 8) | d[25];
        long? ms = rate > 0 && total > 0 ? total * 1000 / rate : null;
        return new MediaProbeResult("FLAC", null, "FLAC", ms, null, null, true, false);
    }

    private static MediaProbeResult ProbeAdts(Stream s)
    {
        var d = new byte[2];
        if (ReadAt(s, 0, d) < 2 || d[0] != 0xFF || (d[1] & 0xF0) != 0xF0) return MediaProbeResult.Unknown;
        return new MediaProbeResult("AAC", null, "AAC", null, null, null, true, false);   // la duración la da el navegador al verificar
    }

    // ─────────────────────────── imágenes ───────────────────────────

    private static MediaProbeResult ProbePng(Stream s)
    {
        var d = new byte[24];
        if (ReadAt(s, 0, d) < 24 || d[0] != 0x89 || d[1] != 'P' || d[2] != 'N' || d[3] != 'G') return MediaProbeResult.Unknown;
        return new MediaProbeResult("PNG", null, null, null, (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(16)), (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(20)), false, false);
    }

    private static MediaProbeResult ProbeJpeg(Stream s)
    {
        var d = new byte[Math.Min(s.Length, 512 * 1024)];
        var n = ReadAt(s, 0, d);
        if (n < 4 || d[0] != 0xFF || d[1] != 0xD8) return MediaProbeResult.Unknown;
        var pos = 2;
        for (var i = 0; i < 4096 && pos + 4 <= n; i++)
        {
            if (d[pos] != 0xFF) { pos++; continue; }
            var marker = d[pos + 1];
            if (marker is 0xFF) { pos++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { pos += 2; continue; }
            var len = BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 2));
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (pos + 9 > n) break;
                return new MediaProbeResult("JPEG", null, null, null, BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 7)), BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(pos + 5)), false, false);
            }
            if (len < 2) break;
            pos += 2 + len;
        }
        return MediaProbeResult.Unknown;
    }

    private static MediaProbeResult ProbeWebp(Stream s)
    {
        var d = new byte[40];
        var n = ReadAt(s, 0, d);
        if (n < 30 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "WEBP") return MediaProbeResult.Unknown;
        var kind = Encoding.ASCII.GetString(d, 12, 4);
        int w, h;
        if (kind == "VP8X") { w = 1 + (d[24] | d[25] << 8 | d[26] << 16); h = 1 + (d[27] | d[28] << 8 | d[29] << 16); }
        else if (kind == "VP8L" && d[20] == 0x2F) { var bits = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(21)); w = (int)(bits & 0x3FFF) + 1; h = (int)((bits >> 14) & 0x3FFF) + 1; }
        else if (kind == "VP8 ") { w = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(26)) & 0x3FFF; h = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(28)) & 0x3FFF; }
        else return MediaProbeResult.Unknown;
        return new MediaProbeResult("WebP", null, null, null, w, h, false, false);
    }

    // ─────────────────────────── utilidades ───────────────────────────

    private static int ReadAt(Stream s, long position, byte[] buffer)
    {
        if (position < 0 || position >= s.Length) return 0;
        s.Position = position;
        var total = 0;
        while (total < buffer.Length)
        {
            var r = s.Read(buffer, total, buffer.Length - total);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }
}
