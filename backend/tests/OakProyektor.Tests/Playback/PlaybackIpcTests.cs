using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Audio;
using OakProyektor.App.Handlers;
using OakProyektor.App.Media;
using OakProyektor.Core.Audio;
using OakProyektor.Core.Media;
using OakProyektor.Core.Output;
using OakProyektor.Data;
using OakProyektor.Data.Media;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.App;
using OakProyektor.Tests.Core;
using OakProyektor.Tests.Data;
using OakProyektor.Tests.Ipc;

namespace OakProyektor.Tests.Playback;

/// <summary>Los mensajes de reproducción y audio de extremo a extremo (despachador + handlers reales + BD real + carpeta temporal): a dónde va cada medio, errores con código, ajustes validados y carriles.</summary>
public sealed class PlaybackIpcTests : IAsyncLifetime
{
    private static readonly AudioDeviceInfo Laptop = new("id-laptop", "Altavoces del portátil");
    private static readonly AudioDeviceInfo Usb = new("id-usb", "Interfaz USB");

    private readonly TestDatabase _db = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "oak-playback-ipc", Guid.NewGuid().ToString("N"));
    private readonly FakeOutputController _output = new();
    private readonly FakeAudioService _audio = new();
    private readonly InMemorySettings _settings = new();
    private readonly FakeDeviceEnumerator _devices = new(Laptop, Usb);
    private readonly FakeMediaFileSystem _fs = new();
    private MediaRepository _repo = null!;
    private MediaStorage _storage = null!;
    private IpcDispatcher _dispatcher = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _storage = new MediaStorage(Path.Combine(_dir, "media"));
        _repo = new MediaRepository(_db.Connections, new AlwaysReady(), null, new MediaRepositoryOptions { WriteTimeoutSeconds = 1 });
        IIpcHandler[] handlers =
        [
            new MediaPlayHandler(_repo, _storage, _fs, _output, _audio, _settings), new MediaPauseHandler(_output, _audio), new MediaResumeHandler(_output, _audio), new MediaSeekHandler(_output, _audio),
            new MediaSetVolumeHandler(_output, _audio), new MediaStopHandler(_output, _audio), new MediaStopAllHandler(_output, _audio), new MediaReportProgressHandler(_output),
            new AudioGetStateHandler(_audio), new AudioSetDeviceHandler(_settings, _audio, _devices), new AudioSetMasterVolumeHandler(_settings, _audio, _output),
            new AudioSetSettingsHandler(_settings, _audio, _output), new AudioTestHandler(_audio),
        ];
        _dispatcher = new IpcDispatcher(handlers, NullLogger<IpcDispatcher>.Instance);
    }

    public Task DisposeAsync()
    {
        _db.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* temporal */ }
        return Task.CompletedTask;
    }

    private static string Msg(string kind, string type, object payload, string id = "c1") =>
        $$"""{"v":{{IpcCatalog.ProtocolVersion}},"id":"{{id}}","kind":"{{kind}}","type":"{{type}}","payload":{{JsonSerializer.Serialize(payload, IpcSerializer.Options)}}}""";

    private async Task<string?> CommandErrorAsync(string type, object payload)
    {
        var reply = await _dispatcher.DispatchAsync(Msg(IpcKind.Command, type, payload), CancellationToken.None);
        return reply is null ? null : reply.GetPayload<CommandFailedPayload>().Error.Code;
    }

    private async Task<MediaItem> SeedAsync(string name, MediaItemKind kind = MediaItemKind.Video, string ext = ".mp4", bool createFile = true)
    {
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))).ToLowerInvariant();
        var item = await _repo.InsertAsync(new NewMediaItem(kind, name, null, name + ext, ext, 100, sha, 5000, 1920, 1080, "MP4", "H.264", "AAC", true, null), default);
        if (createFile)
        {
            var path = _storage.FilePathFor(item);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [1, 2, 3]);
        }
        return item;
    }

    private static MediaPlayPayload Play(long id, bool? start = null, int? volume = null, bool loop = false, ForegroundEndAction onEnd = ForegroundEndAction.Back) =>
        new(id, start, volume, false, loop, null, null, ForegroundBackdrop.Black, onEnd);

    // ───────────── reproducir ─────────────

    [Fact]
    public async Task A_video_goes_to_the_foreground_from_the_local_copy_with_the_settings_fades_and_master_volume()
    {
        await _settings.UpdateAsync(s => s with { Audio = new AudioSettings(MasterVolumePct: 40, VideoFadeInMs: 250, VideoFadeOutMs: 900) }, default);
        var video = await SeedAsync("testimonio");
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaPlay, Play(video.Id, volume: 80)));

        var r = Assert.Single(_output.ForegroundRequests);
        Assert.Equal(MediaPathValidator.ToFileUrl(_storage.FilePathFor(video)), r.Url);   // solo la URL validada de la COPIA local
        Assert.Equal((video.Id, true, 80, 40, 250, 900), (r.MediaId, r.StartPlaying, r.ClipVolumePct, r.MasterVolumePct, r.FadeInMs, r.FadeOutMs));
        Assert.Empty(_audio.Calls);   // un video no pasa por el motor de pistas
    }

    [Fact]
    public async Task A_video_starts_by_default_and_an_audio_track_is_loaded_READY_in_pause_by_default()
    {
        var video = await SeedAsync("video");
        var track = await SeedAsync("musica", MediaItemKind.Audio, ".mp3");
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaPlay, Play(video.Id)));
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaPlay, Play(track.Id)));
        Assert.True(_output.ForegroundRequests[0].StartPlaying);
        Assert.Equal([$"play:{track.Id}:False:0"], _audio.Calls);   // listo en pausa: un sonido nunca arranca sin pedirlo
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaPlay, Play(track.Id, start: true)));
        Assert.Equal($"play:{track.Id}:True:0", _audio.Calls[1]);
    }

    [Fact]
    public async Task Images_missing_files_unknown_ids_and_unsupported_items_are_refused_without_reaching_the_output_or_the_engine()
    {
        var image = await SeedAsync("imagen", MediaItemKind.Image, ".png");
        var gone = await SeedAsync("falta");
        var goneAudio = await SeedAsync("pista", MediaItemKind.Audio, ".mp3", createFile: false);
        var bad = await SeedAsync("roto");
        await _repo.ApplyProbeAsync(bad.Id, false, null, null, null, MediaErrorCodes.VideoCodecUnsupported, default);
        await _repo.SetStatusAsync([gone.Id], MediaStatus.Missing, MediaErrorCodes.MissingFile, default);

        Assert.Equal(ForegroundErrorCodes.NotPlayable, await CommandErrorAsync(IpcTypes.MediaPlay, Play(image.Id)));
        Assert.Equal(MediaErrorCodes.MissingFile, await CommandErrorAsync(IpcTypes.MediaPlay, Play(gone.Id)));
        _fs.Files[Path.GetFullPath(_storage.FilePathFor(goneAudio))] = new FileProbe(false, 0, null);   // la pista ya no está en el disco (su fila aún dice «pendiente»)
        Assert.Equal(MediaErrorCodes.MissingFile, await CommandErrorAsync(IpcTypes.MediaPlay, Play(goneAudio.Id)));
        Assert.Equal(MediaErrorCodes.VideoCodecUnsupported, await CommandErrorAsync(IpcTypes.MediaPlay, Play(bad.Id)));
        Assert.Equal(MediaErrorCodes.NotFound, await CommandErrorAsync(IpcTypes.MediaPlay, Play(999)));
        Assert.Empty(_output.ForegroundRequests);
    }

    [Fact]
    public async Task A_file_that_vanished_from_disk_is_refused_even_if_the_row_still_says_pending()
    {
        var ghost = await SeedAsync("fantasma", createFile: false);
        _fs.Files[Path.GetFullPath(_storage.FilePathFor(ghost))] = new FileProbe(false, 0, null);
        Assert.Equal(MediaErrorCodes.MissingFile, await CommandErrorAsync(IpcTypes.MediaPlay, Play(ghost.Id)));
        Assert.Empty(_output.ForegroundRequests);
    }

    [Theory]
    [InlineData(101, null, null)]
    [InlineData(-1, null, null)]
    [InlineData(null, 5001, null)]
    [InlineData(null, null, -5)]
    public async Task Volume_and_fade_values_out_of_range_are_rejected_with_a_clear_code(int? volume, int? fadeIn, int? fadeOut)
    {
        var video = await SeedAsync("video");
        var payload = new MediaPlayPayload(video.Id, null, volume, false, false, fadeIn, fadeOut, ForegroundBackdrop.Black, ForegroundEndAction.Back);
        Assert.Equal(ForegroundErrorCodes.InvalidRequest, await CommandErrorAsync(IpcTypes.MediaPlay, payload));
        Assert.Empty(_output.ForegroundRequests);
    }

    // ───────────── transporte por destino y Silencio ─────────────

    [Fact]
    public async Task Transport_commands_go_to_the_target_they_name_with_their_arrival_number()
    {
        await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.MediaPause, new MediaTransportPayload(PlaybackTarget.Foreground)), CancellationToken.None);
        await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.MediaPause, new MediaTransportPayload(PlaybackTarget.Track)), CancellationToken.None);
        await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.MediaResume, new MediaTransportPayload(PlaybackTarget.Track)), CancellationToken.None);
        await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.MediaSeek, new MediaSeekPayload(PlaybackTarget.Foreground, 4000)), CancellationToken.None);
        await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.MediaSetVolume, new MediaSetVolumePayload(PlaybackTarget.Track, 30, null)), CancellationToken.None);
        await _dispatcher.DispatchAsync(Msg(IpcKind.Command, IpcTypes.MediaStop, new MediaTransportPayload(PlaybackTarget.Track)), CancellationToken.None);
        Assert.Equal(["fg.pause", "fg.seek:4000"], _output.Calls);
        Assert.Equal(["pause:0", "resume:0", "volume:30", "stop:0"], _audio.Calls);
    }

    [Fact]
    public async Task Silence_quiets_both_the_video_and_the_tracks_and_a_failure_in_one_does_not_prevent_the_other()
    {
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaStopAll, new { }));
        Assert.Equal(["fg.silence"], _output.Calls);
        Assert.Equal(["silence:0"], _audio.Calls);

        // El atajo global llama a la misma función: si el motor de audio falla, el video se calla igualmente (y al revés).
        var failing = new ThrowingAudioService();
        var output2 = new FakeOutputController();
        Silence.Run(output2, failing, 5, null);
        Assert.Equal(["fg.silence"], output2.Calls);
        Assert.Equal([5L], output2.Orders);   // y con el número de llegada del pulso
    }

    [Fact]
    public async Task A_seek_out_of_range_is_rejected_and_the_progress_report_reaches_the_controller()
    {
        Assert.Equal(ForegroundErrorCodes.InvalidRequest, await CommandErrorAsync(IpcTypes.MediaSeek, new MediaSeekPayload(PlaybackTarget.Foreground, -1)));
        Assert.Null(await CommandErrorAsync(IpcTypes.MediaReportProgress, new MediaReportProgressPayload("f1", 1200, 60_000, ReportedPlayback.Playing, null)));
        Assert.Contains("fg.progress:Playing", _output.Calls);
    }

    // ───────────── ajustes de audio ─────────────

    [Fact]
    public async Task Choosing_a_device_stores_its_stable_id_and_name_and_refuses_one_that_is_not_connected()
    {
        Assert.Null(await CommandErrorAsync(IpcTypes.AudioSetDevice, new AudioSetDevicePayload(Usb.Id)));
        Assert.Equal((Usb.Id, Usb.Name), (_settings.Current.AudioConfig.ChurchDeviceId, _settings.Current.AudioConfig.ChurchDeviceName));
        Assert.Contains("settingsApplied", _audio.Calls);

        Assert.Equal(AudioErrorCodes.DeviceNotFound, await CommandErrorAsync(IpcTypes.AudioSetDevice, new AudioSetDevicePayload("id-que-no-existe")));
        Assert.Equal(Usb.Id, _settings.Current.AudioConfig.ChurchDeviceId);   // un fallo no cambia nada

        Assert.Null(await CommandErrorAsync(IpcTypes.AudioSetDevice, new AudioSetDevicePayload(null)));   // «usar el predeterminado de Windows»
        Assert.Null(_settings.Current.AudioConfig.ChurchDeviceId);
        Assert.Null(_settings.Current.AudioConfig.ChurchDeviceName);
    }

    [Fact]
    public async Task Master_volume_is_validated_stored_and_applied_to_the_tracks_and_to_the_video()
    {
        Assert.Null(await CommandErrorAsync(IpcTypes.AudioSetMasterVolume, new AudioSetMasterVolumePayload(65)));
        Assert.Equal(65, _settings.Current.AudioConfig.MasterVolumePct);
        Assert.Contains("settingsApplied", _audio.Calls);
        Assert.Contains("fg.master", _output.Calls);
        Assert.Equal(AudioErrorCodes.InvalidSettings, await CommandErrorAsync(IpcTypes.AudioSetMasterVolume, new AudioSetMasterVolumePayload(150)));
        Assert.Equal(65, _settings.Current.AudioConfig.MasterVolumePct);
    }

    [Fact]
    public async Task Invalid_audio_settings_change_nothing_and_a_disconnected_church_device_is_kept_by_its_id()
    {
        Assert.Equal(AudioErrorCodes.InvalidSettings, await CommandErrorAsync(IpcTypes.AudioSetSettings, new AudioSettingsPayload(null, null, 50, 9000, 0, 0, 0, false)));
        Assert.Equal(new AudioSettings(), _settings.Current.AudioConfig);

        // Un dispositivo guardado que hoy no está conectado se CONSERVA (puede volver): setSettings no lo valida contra los conectados.
        Assert.Null(await CommandErrorAsync(IpcTypes.AudioSetSettings, new AudioSettingsPayload("id-ausente", "Interfaz USB", 70, 100, 200, 300, 400, true)));
        Assert.Equal(new AudioSettings("id-ausente", "Interfaz USB", 70, 100, 200, 300, 400, true), _settings.Current.AudioConfig);
    }

    [Theory]
    [InlineData(AudioTestTarget.Church)]
    [InlineData(AudioTestTarget.Default)]
    public async Task The_test_tone_asks_the_service_for_the_chosen_route(AudioTestTarget target)
    {
        Assert.Null(await CommandErrorAsync(IpcTypes.AudioTest, new AudioTestPayload(target)));
        Assert.Equal([$"test:{target}"], _audio.Calls);
    }

    [Fact]
    public void Pause_stop_and_silence_never_wait_but_play_and_the_test_tone_use_the_slow_lane()
    {
        IpcLane Lane(string type) => IpcPriority.LaneOf(new IpcEnvelope(IpcCatalog.ProtocolVersion, "x", IpcKind.Command, type, default));
        Assert.All(new[] { IpcTypes.MediaPause, IpcTypes.MediaStop, IpcTypes.MediaStopAll }, t => Assert.Equal(IpcLane.Priority, Lane(t)));
        Assert.All(new[] { IpcTypes.MediaPlay, IpcTypes.AudioTest }, t => Assert.Equal(IpcLane.Slow, Lane(t)));
        Assert.All(new[] { IpcTypes.MediaResume, IpcTypes.MediaSeek, IpcTypes.MediaSetVolume, IpcTypes.MediaReportProgress, IpcTypes.AudioSetDevice, IpcTypes.AudioSetMasterVolume }, t => Assert.Equal(IpcLane.Live, Lane(t)));
    }

    private sealed class ThrowingAudioService : IAudioService
    {
        public OakProyektor.Ipc.Messages.AudioStatePayload GetState() => throw new InvalidOperationException();
        public void PlayTrack(long mediaId, string path, long durationMs, bool loop, bool startPlaying, int volumePct, int? fadeInMs, long order) => throw new InvalidOperationException();
        public void PauseTrack(long order) => throw new InvalidOperationException();
        public void ResumeTrack(long order) => throw new InvalidOperationException();
        public void SeekTrack(long positionMs) => throw new InvalidOperationException();
        public void SetTrackVolume(int volumePct) => throw new InvalidOperationException();
        public void StopTrack(long order) => throw new InvalidOperationException();
        public void SilenceTracks(long order) => throw new InvalidOperationException("el motor de audio está colgado");
        public void SettingsApplied() => throw new InvalidOperationException();
        public Task TestToneAsync(AudioTestTarget target, CancellationToken cancellationToken) => throw new InvalidOperationException();
    }
}
