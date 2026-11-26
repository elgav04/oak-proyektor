using Microsoft.Extensions.Logging.Abstractions;
using OakProyektor.App;
using OakProyektor.App.Handlers;
using OakProyektor.Core.Output;
using OakProyektor.Ipc;
using OakProyektor.Ipc.Messages;
using OakProyektor.Tests.Core;

namespace OakProyektor.Tests.App;

/// <summary>Los comandos multimedia de la salida (v7): validan en el backend y solo entonces llegan al controlador.</summary>
public class MediaHandlerTests
{
    private const string Theme = """{"fontFamily":"Arial","fontSize":72,"color":"#FFFFFF","outlineWidth":0,"outlineColor":"#000000","shadowBlur":0,"shadowOffset":0,"shadowColor":"#000000","align":"center","verticalAlign":"middle","lineHeight":1.2,"marginTop":50,"marginRight":50,"marginBottom":50,"marginLeft":50,"autoFit":true,"minFontSize":24}""";

    private static string Msg(string type, string payload) => $$"""{"v":15,"id":"c1","kind":"command","type":"{{type}}","payload":{{payload}}}""";

    private static (IpcDispatcher D, FakeOutputController Output, FakeMediaFileSystem Fs) Create(IMediaFileSystem? fs = null, TimeSpan? timeout = null)
    {
        var output = new FakeOutputController();
        var fake = fs as FakeMediaFileSystem ?? new FakeMediaFileSystem();
        IIpcHandler[] handlers =
        [
            new OutputSetBackgroundHandler(output, fs ?? fake, timeout),
            new OutputShowContentHandler(output),
            new OutputClearContentHandler(output),
            new OutputReportBackgroundFailureHandler(output),
            new OutputRetryBackgroundHandler(output),
        ];
        return (new IpcDispatcher(handlers, NullLogger<IpcDispatcher>.Instance), output, fake);
    }

    private static string? ErrorCode(IpcEnvelope? response) => response is null ? null : response.GetPayload<CommandFailedPayload>().Error.Code;
    private static string? ErrorMessage(IpcEnvelope? response) => response is null ? null : response.GetPayload<CommandFailedPayload>().Error.Message;

    private static string Video(string path, string extra = "") =>
        $$"""{"kind":"video","path":{{System.Text.Json.JsonSerializer.Serialize(path)}},"color":"#102030","muted":true,"transitionMs":600{{extra}}}""";

    [Fact]
    public async Task The_json_the_ps_script_sends_deserializes_as_is()
    {
        var env = IpcSerializer.TryParse("""{"v":15,"id":"x","kind":"command","type":"output.setBackground","payload":{"color":"#102030","path":"C:\\Users\\x\\media-test\\a.mp4","transitionMs":300,"muted":true,"kind":"video"}}""", out var e, out var err);
        Assert.True(env, err);
        var p = e!.GetPayload<OutputSetBackgroundPayload>();
        Assert.Equal((BackgroundKind.Video, 300), (p.Kind, p.TransitionMs));
    }

    [Fact]
    public async Task A_valid_video_reaches_the_output_with_only_the_validated_file_url()
    {
        var (d, output, _) = Create();
        var response = await d.DispatchAsync(Msg("output.setBackground", Video(@"C:\Videos\Mi fondo.mp4")), CancellationToken.None);

        Assert.Null(response);
        var bg = Assert.Single(output.Backgrounds);
        Assert.Equal((BackgroundKind.Video, "file:///C:/Videos/Mi%20fondo.mp4", "Mi fondo.mp4", "#102030", true), (bg.Kind, bg.MediaUrl, bg.FileName, bg.Color, bg.Muted));
        Assert.Null(bg.Warning);
    }

    [Theory]
    [InlineData(@"\\servidor\r\a.mp4", "output.mediaRejected")]
    [InlineData(@"C:\falta.mp4", "output.mediaRejected")]
    [InlineData(@"C:\a.mkv", "output.mediaRejected")]
    [InlineData(@"relativa\a.mp4", "output.mediaRejected")]
    [InlineData(@"C:\a.png", "output.mediaRejected")]     // se pidió video
    public async Task A_rejected_path_answers_media_rejected_and_never_reaches_the_output(string path, string code)
    {
        var fs = new FakeMediaFileSystem();
        fs.Files[@"C:\falta.mp4"] = new FileProbe(false, 0, null);
        var (d, output, _) = Create(fs);

        var response = await d.DispatchAsync(Msg("output.setBackground", Video(path)), CancellationToken.None);

        Assert.Equal(code, ErrorCode(response));
        Assert.NotEmpty(ErrorMessage(response)!);
        Assert.Empty(output.Backgrounds);
    }

    [Fact]
    public async Task The_rejection_message_gives_the_reason()
    {
        var (d, _, _) = Create();
        Assert.Contains("red", ErrorMessage(await d.DispatchAsync(Msg("output.setBackground", Video(@"\\srv\x\a.mp4")), CancellationToken.None)));
        Assert.Contains("no soportado", ErrorMessage(await d.DispatchAsync(Msg("output.setBackground", Video(@"C:\a.mkv")), CancellationToken.None)));
    }

    [Fact]
    public async Task A_file_on_a_removable_drive_is_accepted_with_a_warning_for_the_panel()
    {
        var fs = new FakeMediaFileSystem();
        fs.Drives[@"E:\"] = DriveType.Removable;
        var (d, output, _) = Create(fs);

        await d.DispatchAsync(Msg("output.setBackground", Video(@"E:\fondo.mp4")), CancellationToken.None);

        Assert.Equal(BackgroundLayer.RemovableDriveWarning, Assert.Single(output.Backgrounds).Warning);
    }

    [Fact]
    public async Task A_stuck_disk_does_not_hold_the_inbound_lane_forever()
    {
        var gate = new ManualResetEventSlim();
        var (d, output, _) = Create(new BlockingFileSystem(gate), TimeSpan.FromMilliseconds(100));
        try
        {
            var response = await d.DispatchAsync(Msg("output.setBackground", Video(@"C:\a.mp4")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("output.mediaRejected", ErrorCode(response));
            Assert.Empty(output.Backgrounds);
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task Color_and_none_backgrounds_need_no_file_and_validate_the_color()
    {
        var (d, output, fs) = Create();
        await d.DispatchAsync(Msg("output.setBackground", """{"kind":"color","path":null,"color":"#336699","muted":true,"transitionMs":300}"""), CancellationToken.None);
        await d.DispatchAsync(Msg("output.setBackground", """{"kind":"none","path":null,"color":null,"muted":true,"transitionMs":0}"""), CancellationToken.None);
        Assert.Equal([BackgroundKind.Color, BackgroundKind.None], output.Backgrounds.Select(b => b.Kind));
        Assert.Equal("#336699", output.Backgrounds[0].Color);
        Assert.Empty(fs.Probed);

        var bad = await d.DispatchAsync(Msg("output.setBackground", """{"kind":"color","path":null,"color":"rojo","muted":true,"transitionMs":300}"""), CancellationToken.None);
        Assert.Equal("output.invalidContent", ErrorCode(bad));
        var badMs = await d.DispatchAsync(Msg("output.setBackground", """{"kind":"color","path":null,"color":"#336699","muted":true,"transitionMs":99999}"""), CancellationToken.None);
        Assert.Equal("output.invalidContent", ErrorCode(badMs));
        Assert.Equal(2, output.Backgrounds.Count);
    }

    [Fact]
    public async Task Show_content_validates_the_slide_before_the_output_sees_it()
    {
        var (d, output, _) = Create();
        Assert.Null(await d.DispatchAsync(Msg("output.showContent", $$"""{"blocks":["Uno","Dos"],"theme":{{Theme}},"transitionMs":400}"""), CancellationToken.None));
        Assert.Equal(["showContent:2:400"], output.Calls);

        var empty = await d.DispatchAsync(Msg("output.showContent", $$"""{"blocks":[],"theme":{{Theme}},"transitionMs":400}"""), CancellationToken.None);
        var badTheme = await d.DispatchAsync(Msg("output.showContent", """{"blocks":["x"],"theme":{"fontFamily":"Arial; color:red","fontSize":72,"color":"#FFFFFF","outlineWidth":0,"outlineColor":"#000000","shadowBlur":0,"shadowOffset":0,"shadowColor":"#000000","align":"center","verticalAlign":"middle","lineHeight":1.2,"marginTop":50,"marginRight":50,"marginBottom":50,"marginLeft":50,"autoFit":true,"minFontSize":24},"transitionMs":400}"""), CancellationToken.None);
        Assert.Equal("output.invalidContent", ErrorCode(empty));
        Assert.Equal("output.invalidContent", ErrorCode(badTheme));
        Assert.Single(output.Calls);
    }

    [Fact]
    public async Task Clear_content_and_failure_reports_reach_the_controller()
    {
        var (d, output, _) = Create();
        await d.DispatchAsync(Msg("output.clearContent", """{"transitionMs":250}"""), CancellationToken.None);
        await d.DispatchAsync(Msg("output.reportBackgroundFailure", """{"mediaUrl":"file:///C:/a.mp4","reason":"decodeError","detail":"MEDIA_ERR_DECODE"}"""), CancellationToken.None);
        Assert.Equal(["clearContent:250", "backgroundFailure:DecodeError:file:///C:/a.mp4"], output.Calls);

        var bad = await d.DispatchAsync(Msg("output.reportBackgroundFailure", """{"mediaUrl":"x","reason":"inventado","detail":null}"""), CancellationToken.None);
        Assert.NotNull(bad);   // un motivo desconocido no llega al controlador
        Assert.Equal(2, output.Calls.Count);
    }

    [Fact]
    public async Task A_stuck_disk_never_accumulates_blocked_threads_one_check_in_flight_at_most()
    {
        var gate = new ManualResetEventSlim();
        var blocking = new BlockingFileSystem(gate);
        var (d, output, _) = Create(blocking, TimeSpan.FromMilliseconds(100));
        try
        {
            var first = await d.DispatchAsync(Msg("output.setBackground", Video(@"C:\a.mp4")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("output.mediaRejected", ErrorCode(first));

            // La primera comprobación sigue colgada en el disco: las siguientes se rechazan AL INSTANTE y NO ocupan otro hilo del pool.
            for (var i = 0; i < 20; i++)
            {
                var again = await d.DispatchAsync(Msg("output.setBackground", Video(@"C:\a.mp4")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal("output.mediaRejected", ErrorCode(again));
                Assert.Contains("anterior", ErrorMessage(again));
            }
            Assert.Equal(1, blocking.Calls);
            Assert.Empty(output.Backgrounds);
        }
        finally { gate.Set(); }

        // Cuando el disco responde, la guarda se libera y se vuelve a poder aplicar un fondo.
        await Task.Delay(200);
        var ok = await d.DispatchAsync(Msg("output.setBackground", Video(@"C:\a.mp4")), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(ok);
        Assert.Single(output.Backgrounds);
    }

    [Fact]
    public async Task Retry_background_reaches_the_controller_and_needs_no_payload()
    {
        var (d, output, _) = Create();
        Assert.Null(await d.DispatchAsync("""{"v":15,"id":"r1","kind":"command","type":"output.retryBackground","payload":{}}""", CancellationToken.None));
        Assert.Equal(["retryBackground"], output.Calls);
    }

    private sealed class BlockingFileSystem(ManualResetEventSlim gate) : IMediaFileSystem
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public DriveType DriveTypeOf(string root) => DriveType.Fixed;
        public FileProbe Probe(string path) { Interlocked.Increment(ref _calls); gate.Wait(TimeSpan.FromSeconds(30)); return new FileProbe(true, 1, null); }
    }
}
