# Prueba REAL de la reproduccion y el audio (protocolo v18) sobre la APLICACION REAL (compilacion Debug), con la depuracion remota de WebView2 SOLO para esta prueba. La salida se abre en una
# VENTANA NORMAL (nunca en el proyector); los datos van a una carpeta temporal (OAK_DATA_DIR). NINGUN medio de terceros: el video (con una pista de audio de 330 Hz) se GENERA dentro de la
# propia salida con MediaRecorder y la pista es un WAV sintetico de 440 Hz.
#
# ATENCION: esta prueba HACE SONAR el dispositivo de audio predeterminado de Windows durante unos segundos (volumen maestro 25 %, tonos suaves). El sonido se comprueba SIN oidos con el
# MEDIDOR DE PICOS de Windows (IAudioMeterInformation del dispositivo: «hay senal» antes y «cero» despues). No cambia el dispositivo predeterminado de Windows (lo comprueba).
#
# Comprueba: el video en primer plano sobre el fondo en bucle (mismo nodo y currentTime continuo del fondo, fotogramas de los dos, orden de las capas con la alerta ENCIMA), Texto limpio / Negro /
# Logo y la pausa de la salida (pausan y silencian conservando la posicion, volver no reanuda), Freeze (no lo congela), buscar y volumen, el fin del video (volver o quedarse), detener (libera el
# elemento), las pistas de audio (suenan por el dispositivo, NO se cortan con Negro ni con la pausa de la salida), «Silencio» con la tecla REAL Ctrl+Alt+Shift+S (calla pistas y video, un
# segundo pulso no reanuda), el tono de prueba por las dos rutas, la ruta de audio y su aviso, y que el registro no contiene nombres ni rutas del operador.
#   powershell -File tools\media-playback-test.ps1 [-KeepData]
param(
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$protoV = [regex]::Match((Get-Content (Join-Path $root "frontend\src\ipc\protocol.ts") -Raw), 'PROTOCOL_VERSION\s*=\s*(\d+)').Groups[1].Value
if (-not $protoV) { throw 'No se pudo leer PROTOCOL_VERSION de frontend\src\ipc\protocol.ts' }
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
if (-not $exe) { throw "Compila en Debug primero (npm run build en frontend/ y dotnet build)." }

# ───────── Medidor de picos de Windows (tools\AudioMeter.cs: COM directo, sin librerias de terceros) ─────────
Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot "AudioMeter.cs") -Raw)
Add-Type -Namespace Probe -Name Keys -MemberDefinition '[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);'
function Press-Hotkey([byte]$vk) {
    foreach ($m in 0x11, 0x12, 0x10) { [Probe.Keys]::keybd_event($m, 0, 0, [UIntPtr]::Zero) }
    [Probe.Keys]::keybd_event($vk, 0, 0, [UIntPtr]::Zero); [Probe.Keys]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
    foreach ($m in 0x10, 0x12, 0x11) { [Probe.Keys]::keybd_event($m, 0, 2, [UIntPtr]::Zero) }
}
# Procesos de ESTA aplicacion (el ejecutable y los procesos de su WebView2): el medidor por sesion solo cuenta su sonido, no el de otras aplicaciones del equipo.
function Get-OakPids {
    $match = [regex]::Escape($data)
    $ids = New-Object System.Collections.Generic.List[int]; $ids.Add($proc.Id)
    Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" | Where-Object { $_.CommandLine -match $match } | ForEach-Object { $ids.Add([int]$_.ProcessId) }
    return ,$ids.ToArray()
}
# Pico maximo del sonido de la APLICACION en el dispositivo durante $ms milisegundos (muestreo cada 25 ms). Sin dispositivo = el predeterminado de Windows.
function Get-PeakFor([int]$ms, [string]$deviceId = "") {
    $pids = Get-OakPids
    $max = 0.0; $end = [DateTime]::UtcNow.AddMilliseconds($ms)
    while ([DateTime]::UtcNow -lt $end) { $p = [double][PeakMeter]::SessionPeak($deviceId, $pids); if ($p -gt $max) { $max = $p }; Start-Sleep -Milliseconds 25 }
    return $max
}
# Pico del dispositivo ENTERO (incluye otras aplicaciones): solo informativo.
function Get-EndpointPeakFor([int]$ms) { $max = 0.0; $end = [DateTime]::UtcNow.AddMilliseconds($ms); while ([DateTime]::UtcNow -lt $end) { $p = [double][PeakMeter]::Peak(""); if ($p -gt $max) { $max = $p }; Start-Sleep -Milliseconds 25 }; return $max }

$defaultBefore = [PeakMeter]::DefaultId()
$data = Join-Path $env:TEMP ("oak-playback-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $data | Out-Null
$env:OAK_DATA_DIR = $data; $env:OAK_NO_DIALOG = "1"
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=0"
$proc = Start-Process $exe.FullName -PassThru
Remove-Item Env:\WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS

function Get-PortFor($which) {
    $dir = if ($which -eq "output") { "WebView2-output" } else { "WebView2" }
    $file = Join-Path $data "cache\$dir\EBWebView\DevToolsActivePort"
    if (Test-Path $file) { [int](Get-Content $file | Select-Object -First 1) } else { 0 }
}
function Get-Targets($which = "control") {
    $p = Get-PortFor $which; if ($p -eq 0) { return @() }
    try { Invoke-RestMethod "http://127.0.0.1:$p/json" -TimeoutSec 2 } catch { @() }
}
function Connect-Cdp($url) {
    $ws = New-Object System.Net.WebSockets.ClientWebSocket
    $ws.ConnectAsync([Uri][string]($url | Select-Object -First 1), [Threading.CancellationToken]::None).Wait(5000) | Out-Null
    return $ws
}
$script:cdpId = 0
function Invoke-Cdp($ws, $method, $params = @{}, $timeoutMs = 8000) {
    $script:cdpId++
    $id = $script:cdpId
    $json = @{ id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
    $ws.SendAsync([ArraySegment[byte]][Text.Encoding]::UTF8.GetBytes($json), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait(5000) | Out-Null
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    $buffer = New-Object byte[] 262144
    while ([DateTime]::UtcNow -lt $deadline) {
        $sb = New-Object Text.StringBuilder
        do {
            $left = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
            if ($left -le 0) { return $null }
            $cts = New-Object Threading.CancellationTokenSource($left)
            try { $r = $ws.ReceiveAsync([ArraySegment[byte]]$buffer, $cts.Token).GetAwaiter().GetResult() } catch { return $null }
            [void]$sb.Append([Text.Encoding]::UTF8.GetString($buffer, 0, $r.Count))
        } while (-not $r.EndOfMessage)
        $msg = $sb.ToString() | ConvertFrom-Json
        if ($msg.id -eq $id) { return $msg }
    }
    return $null
}
function Eval($ws, $expr, $timeoutMs = 8000) {
    $expr = $expr.Replace('@@V@@', $protoV)   # la version del protocolo sale de frontend\src\ipc\protocol.ts: nunca un numero a mano
    $r = Invoke-Cdp $ws "Runtime.evaluate" @{ expression = $expr; awaitPromise = $true; returnByValue = $true } $timeoutMs
    if ($null -eq $r) { return $null }
    if ($r.result.exceptionDetails) { throw ("Error en la pagina: " + ($r.result.exceptionDetails | ConvertTo-Json -Depth 4 -Compress)) }
    return $r.result.result.value
}
function Send-Command($ws, [string]$type, $payload) {
    $json = $payload | ConvertTo-Json -Depth 8 -Compress
    [void](Eval $ws ("window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'$type',payload:$json}));true"))
}
# Pregunta (query) al backend DESDE la pagina indicada y devuelve el result como objeto.
function Ask($ws, [string]$type, $payload = @{}) {
    $json = $payload | ConvertTo-Json -Depth 8 -Compress
    $raw = Eval $ws "(async () => { const id = crypto.randomUUID(); return await new Promise((resolve) => { window.external.receiveMessage((raw) => { const m = JSON.parse(raw); if (m.id === id) resolve(JSON.stringify(m.payload)); }); window.external.sendMessage(JSON.stringify({ v:@@V@@, id, kind: 'query', type: '$type', payload: $json })); setTimeout(() => resolve('null'), 6000); }); })()" 10000
    if (-not $raw -or $raw -eq "null") { return $null }
    return ($raw | ConvertFrom-Json).result
}
function Log-Text { (Get-ChildItem "$data\logs" -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n" }

function New-TestVideo($ws, [string]$path, [int]$seconds, [int]$hueBase) {
    $js = @"
(async () => {
  const mime = 'video/mp4;codecs=avc1.640028,mp4a.40.2';
  const withAudio = MediaRecorder.isTypeSupported(mime);
  const useMime = withAudio ? mime : 'video/mp4;codecs=avc1.640028';
  if (!MediaRecorder.isTypeSupported(useMime)) return 'UNSUPPORTED';
  const c = document.createElement('canvas'); c.width = 1920; c.height = 1080; document.body.appendChild(c);
  const g = c.getContext('2d'); let f = 0, run = true;
  (function draw() { if (!run) return; f++;
    g.fillStyle = 'hsl(' + (($hueBase + f * 3) % 360) + ',60%,35%)'; g.fillRect(0, 0, 1920, 1080);
    g.fillStyle = '#fff'; g.font = 'bold 180px sans-serif'; g.fillText('F' + String(f).padStart(4, '0'), 120, 540);
    requestAnimationFrame(draw); })();
  const stream = c.captureStream(30);
  let ac = null;
  if (withAudio) { ac = new AudioContext(); await ac.resume(); const osc = ac.createOscillator(); osc.frequency.value = 330; const gain = ac.createGain(); gain.gain.value = 0.3; const dest = ac.createMediaStreamDestination(); osc.connect(gain); gain.connect(dest); osc.start(); dest.stream.getAudioTracks().forEach((t) => stream.addTrack(t)); }
  const rec = new MediaRecorder(stream, { mimeType: useMime, videoBitsPerSecond: 6e6 });
  const chunks = []; rec.ondataavailable = (e) => e.data.size && chunks.push(e.data);
  const stopped = new Promise((r) => { rec.onstop = r; });
  rec.start(1000); await new Promise((r) => setTimeout(r, $seconds * 1000)); rec.stop(); await stopped; run = false; c.remove(); if (ac) ac.close();
  const blob = new Blob(chunks);
  const b64 = await new Promise((resolve) => { const fr = new FileReader(); fr.onload = () => resolve(String(fr.result).split(',')[1]); fr.readAsDataURL(blob); });
  return (withAudio ? 'A:' : 'V:') + b64;
})()
"@
    $r = Eval $ws $js (($seconds + 40) * 1000)
    if (-not $r -or $r -eq "UNSUPPORTED") { throw "No se pudo generar el MP4 de prueba (MediaRecorder sin H.264)." }
    [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($r.Substring(2)))
    return $r.StartsWith("A:")
}
function New-TestWav([string]$path, [int]$seconds) {
    $rate = 22050; $samples = $rate * $seconds
    $bytes = New-Object byte[] (44 + $samples * 2)
    [Text.Encoding]::ASCII.GetBytes("RIFF").CopyTo($bytes, 0); [BitConverter]::GetBytes([int](36 + $samples * 2)).CopyTo($bytes, 4); [Text.Encoding]::ASCII.GetBytes("WAVEfmt ").CopyTo($bytes, 8)
    [BitConverter]::GetBytes([int]16).CopyTo($bytes, 16); [BitConverter]::GetBytes([int16]1).CopyTo($bytes, 20); [BitConverter]::GetBytes([int16]1).CopyTo($bytes, 22); [BitConverter]::GetBytes([int]$rate).CopyTo($bytes, 24)
    [BitConverter]::GetBytes([int]($rate * 2)).CopyTo($bytes, 28); [BitConverter]::GetBytes([int16]2).CopyTo($bytes, 32); [BitConverter]::GetBytes([int16]16).CopyTo($bytes, 34)
    [Text.Encoding]::ASCII.GetBytes("data").CopyTo($bytes, 36); [BitConverter]::GetBytes([int]($samples * 2)).CopyTo($bytes, 40)
    for ($i = 0; $i -lt $samples; $i++) { [BitConverter]::GetBytes([int16](12000 * [Math]::Sin(2 * [Math]::PI * 440 * $i / $rate))).CopyTo($bytes, 44 + 2 * $i) }
    [IO.File]::WriteAllBytes($path, $bytes)
}

$result = [ordered]@{}
$fails = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FALLA $what" -ForegroundColor Red; $script:fails.Add($what) } }
$SECRET = "SECRETO-OPERADOR"
try {
    $control = $null
    for ($i = 0; $i -lt 40 -and -not $control; $i++) { Start-Sleep -Milliseconds 500; $control = Get-Targets "control" | Where-Object { $_.type -eq "page" -and $_.url -match "index\.html" } | Select-Object -First 1 }
    if (-not $control) { throw "No aparecio el panel" }
    $cws = Connect-Cdp $control.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $list = Ask $cws "display.list"
    $panelId = ($list.displays | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    Send-Command $cws "output.open" @{ displayId = $panelId }
    $output = $null
    for ($i = 0; $i -lt 40 -and -not $output; $i++) { Start-Sleep -Milliseconds 500; $output = Get-Targets "output" | Where-Object { $_.type -eq "page" -and $_.url -match "output\.html" } | Select-Object -First 1 }
    if (-not $output) { throw "No aparecio la ventana de salida" }
    $ows = Connect-Cdp $output.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $result.chromium = Eval $ows "navigator.userAgent"
    Send-Command $cws "output.setMaster" @{ master = "live" }
    Send-Command $cws "audio.setMasterVolume" @{ volumePct = 25 }   # suave: la prueba suena de verdad
    $result.ambientEndpointPeakBeforeTest = [math]::Round((Get-EndpointPeakFor 400), 4)   # lo que suena en el equipo ANTES (otras aplicaciones): por eso se mide por sesion de la aplicacion

    # ───────── Material generado e importado por la biblioteca real ─────────
    $src = Join-Path $data "origen"; New-Item -ItemType Directory $src | Out-Null
    Write-Host "Generando material (video H.264 de 6 s con audio y una pista WAV de 8 s)..."
    $videoFile = Join-Path $src "$SECRET video.mp4"
    $videoHasAudio = New-TestVideo $ows $videoFile 6 40
    $bgFile = Join-Path $src "$SECRET fondo.mp4"
    [void](New-TestVideo $ows $bgFile 4 200)
    $wavFile = Join-Path $src "$SECRET pista.wav"; New-TestWav $wavFile 8
    $result.videoHasAudioTrack = $videoHasAudio
    foreach ($f in $videoFile, $bgFile, $wavFile) { Send-Command $cws "media.import" @{ path = $f; name = $null; folderId = $null }; Start-Sleep -Seconds 3 }
    $items = $null
    for ($i = 0; $i -lt 30; $i++) { $items = (Ask $cws "media.list" @{ folderId = $null; onlyUnfiled = $false; kind = $null; query = $null; limit = $null }).items; if ($items.Count -ge 3) { break }; Start-Sleep -Seconds 1 }
    $video = $items | Where-Object { $_.originalFileName -like "*$SECRET video.mp4" } | Select-Object -First 1
    $bg = $items | Where-Object { $_.originalFileName -like "*$SECRET fondo.mp4" } | Select-Object -First 1
    $wav = $items | Where-Object { $_.kind -eq "audio" } | Select-Object -First 1
    Check ($video -and $bg -and $wav) "se importaron el video, el fondo y la pista"
    if (-not ($video -and $bg -and $wav)) { throw "sin material" }
    Start-Sleep -Seconds 2   # el panel verifica cada archivo en su reproductor oculto

    # ───────── 1. Fondo en bucle + video en primer plano ─────────
    Write-Host "Video en primer plano sobre el fondo en bucle"
    Send-Command $cws "media.setBackground" @{ mediaId = $bg.id; muted = $true; transitionMs = 0 }
    Start-Sleep -Seconds 2
    $bgBefore = Eval $ows '(() => { const v = document.querySelector("[data-layer=background] video"); window.__bgNode = v; return JSON.stringify({ has: !!v, paused: v && v.paused, t: v && v.currentTime, muted: v && v.muted }); })()' | ConvertFrom-Json
    Check ($bgBefore.has -and -not $bgBefore.paused) "el fondo en bucle esta reproduciendose"
    $base = Eval $ows '(async () => { const b = document.querySelector("[data-layer=background] video"); let n = 0; (function c() { n++; b.requestVideoFrameCallback(c); })(); await new Promise((r) => setTimeout(r, 2000)); return n; })()' 6000
    $result.backgroundFramesAloneIn2s = $base
    Send-Command $cws "media.play" @{ mediaId = $video.id; startPlaying = $true; volumePct = 100; muted = $false; loop = $true; fadeInMs = $null; fadeOutMs = $null; backdrop = "black"; onEnd = "stay" }   # en bucle: las mediciones siguientes no dependen de que el video no termine
    Start-Sleep -Seconds 2
    $fg = Eval $ows '(() => { const v = document.querySelector("[data-layer=foreground] video"); const b = document.querySelector("[data-layer=background] video"); window.__fgNode = v; return JSON.stringify({ has: !!v, src: v && v.getAttribute("src"), paused: v && v.paused, t: v && v.currentTime, ready: v && v.readyState, vol: v && v.volume, muted: v && v.muted, op: v && v.parentElement.style.opacity, sameBg: b === window.__bgNode, bgPaused: b && b.paused }); })()' | ConvertFrom-Json
    Check ($fg.has -and -not $fg.paused) "el video en primer plano se reproduce"
    Check ($fg.src -match "^file:///.*media/files/") "se reproduce desde la COPIA local de la biblioteca ($($fg.src.Substring(0, [Math]::Min(60, $fg.src.Length)))...)"
    Check ($fg.op -eq "1") "es visible (opacidad 1)"
    Check ([Math]::Abs($fg.vol - 0.25) -lt 0.03) "volumen efectivo = 100 % del clip x 25 % maestro ($($fg.vol))"
    Check ($fg.sameBg -and -not $fg.bgPaused) "el fondo sigue siendo el MISMO nodo y sigue reproduciendose debajo"
    $frames = Eval $ows '(async () => { const f = document.querySelector("[data-layer=foreground] video"), b = document.querySelector("[data-layer=background] video"); let nf = 0, nb = 0; const t0 = f.currentTime, tb0 = b.currentTime; (function a() { nf++; f.requestVideoFrameCallback(a); })(); (function c() { nb++; b.requestVideoFrameCallback(c); })(); await new Promise((r) => setTimeout(r, 2000)); return JSON.stringify({ fg: nf, bg: nb, fgAdvance: f.currentTime - t0, bgAdvance: b.currentTime - tb0 }); })()' 6000 | ConvertFrom-Json
    $result.framesIn2s = $frames
    # Ninguno de los dos se congela (>= 15 fps), y se REGISTRA cuanto pierden por convivir: es el dato que decide si hace falta «pausar el fondo mientras el primer plano lo cubre» (docs/DISENO_MEDIOS.md §3.2).
    Check ($frames.fg -ge 15 -and $frames.bg -ge 15) "los dos videos presentan fotogramas a la vez (ninguno se congela: >= 7,5 fps) (primer plano $($frames.fg), fondo $($frames.bg) en 2 s; el fondo solo: $base)"
    $fgPaused = Eval $ows '(async () => { const f = document.querySelector("[data-layer=foreground] video"), b = document.querySelector("[data-layer=background] video"); b.pause(); await new Promise((r) => setTimeout(r, 400)); let n = 0; (function a() { n++; f.requestVideoFrameCallback(a); })(); await new Promise((r) => setTimeout(r, 2000)); b.play(); return n; })()' 8000
    $result.foregroundFramesWithBackgroundPaused = $fgPaused
    $lossPct = [math]::Round(100 * (1 - (($frames.fg + $frames.bg) / 2) / [math]::Max(1, $base)))
    $result.framesLostByCoexistingPct = $lossPct
    if ($lossPct -ge 10) { Write-Host "  AVISO con los dos videos a la vez cada uno presenta ~$lossPct % menos fotogramas que el fondo solo ($base en 2 s); con el fondo en pausa el primer plano presenta $fgPaused. Decidir en el proyector real si conviene pausar el fondo mientras el primer plano lo cubre." -ForegroundColor Yellow }
    $order = Eval $ows '(() => { const z = (s) => Number(getComputedStyle(document.querySelector(s)).zIndex) || 0; return JSON.stringify({ fg: z("[data-layer=foreground]"), overlays: z("[data-layer=overlays]"), black: z("[data-layer=black]"), top: (document.elementsFromPoint(960, 540)[0] || {}).tagName }); })()' | ConvertFrom-Json
    Check ($order.overlays -gt $order.fg -and $order.black -gt $order.overlays) "orden de las capas: video < alerta < cortinas ($($order.fg) < $($order.overlays) < $($order.black))"

    $pk = Get-PeakFor 800
    $result.peakVideoPlaying = $pk
    if ($videoHasAudio) {
        Check ($pk -gt 0.002) "se OYE el video: pico de la aplicacion $([math]::Round($pk, 4))"
        if ($pk -le 0.002) { Write-Host ("    (sesiones de audio del dispositivo: " + [PeakMeter]::Sessions("") + "; procesos de la aplicacion: " + ((Get-OakPids) -join ",") + "; audio del elemento: muted=" + (Eval $ows 'document.querySelector("[data-layer=foreground] video").muted') + " vol=" + (Eval $ows 'document.querySelector("[data-layer=foreground] video").volume') + " hasAudio=" + $video.hasAudio + " codec=" + $video.audioCodec + " audioBytes=" + (Eval $ows 'document.querySelector("[data-layer=foreground] video").webkitAudioDecodedByteCount') + ")") }
    } else { Write-Host "  (el video generado no tiene audio: no se mide su sonido)" }

    # ───────── 2. Controles maestros y pausa de la salida ─────────
    Write-Host "Controles maestros"
    foreach ($m in "black", "clearText", "logo") {
        Send-Command $cws "output.setMaster" @{ master = $m }
        Start-Sleep -Milliseconds 900
        $s1 = Eval $ows '(() => { const v = document.querySelector("[data-layer=foreground] video"); return JSON.stringify({ same: v === window.__fgNode, paused: v.paused, t: v.currentTime, vol: v.volume, op: v.parentElement.style.opacity }); })()' | ConvertFrom-Json
        Start-Sleep -Milliseconds 500
        $t2 = [double](Eval $ows 'document.querySelector("[data-layer=foreground] video").currentTime')
        Check ($s1.same -and $s1.paused -and $s1.op -eq "0") "$m : el video se oculta y se PAUSA (mismo elemento)"
        Check ([Math]::Abs($t2 - $s1.t) -lt 0.05) "$m : conserva la posicion ($([math]::Round($s1.t,2)) s)"
        if ($videoHasAudio) { Check ((Get-PeakFor 500) -lt 0.001) "$m : silencio real (pico ~0)" }
        Send-Command $cws "output.setMaster" @{ master = "live" }
        Start-Sleep -Milliseconds 900
        $s2 = Eval $ows '(() => { const v = document.querySelector("[data-layer=foreground] video"); return JSON.stringify({ paused: v.paused, op: v.parentElement.style.opacity }); })()' | ConvertFrom-Json
        Check ($s2.paused -and $s2.op -eq "1") "$m -> En vivo : se ve de nuevo pero NO se reanuda solo"
    }
    Send-Command $cws "media.resume" @{ target = "foreground" }
    Start-Sleep -Milliseconds 800
    Check (-not [bool](Eval $ows 'document.querySelector("[data-layer=foreground] video").paused')) "reanudar es una accion del operador: vuelve a reproducirse"
    Send-Command $cws "media.seek" @{ target = "foreground"; positionMs = 800 }   # lejos del final: que un fin del video no se confunda con un congelado
    Start-Sleep -Milliseconds 700
    Send-Command $cws "output.setMaster" @{ master = "freeze" }
    Start-Sleep -Milliseconds 800
    $fz = Eval $ows '(() => { const v = document.querySelector("[data-layer=foreground] video"); const t = v.currentTime; return JSON.stringify({ paused: v.paused, t, op: v.parentElement.style.opacity }); })()' | ConvertFrom-Json
    Start-Sleep -Milliseconds 700
    $fz2 = [double](Eval $ows 'document.querySelector("[data-layer=foreground] video").currentTime')
    Check ((-not $fz.paused) -and $fz.op -eq "1" -and $fz2 -gt $fz.t) "Freeze NO congela el video en primer plano (sigue avanzando: $([math]::Round($fz.t,2)) -> $([math]::Round($fz2,2)))"
    Send-Command $cws "media.pause" @{ target = "foreground" }
    Start-Sleep -Milliseconds 700
    Check ([bool](Eval $ows 'document.querySelector("[data-layer=foreground] video").paused')) "con Freeze activo se puede pausar"
    Send-Command $cws "output.setMaster" @{ master = "live" }
    Send-Command $cws "media.resume" @{ target = "foreground" }

    Write-Host "Pausa de la salida (recuperacion manual)"
    Send-Command $cws "output.recover" @{ reason = "manual" }
    Start-Sleep -Milliseconds 1500
    $st = (Ask $cws "output.getState").state
    Check ($st.foreground.phase -eq "paused") "con la salida en pausa el video queda en pausa en el ESTADO (conserva su posicion)"
    Send-Command $cws "output.open" @{ displayId = $panelId }
    Start-Sleep -Milliseconds 1500
    $st = (Ask $cws "output.getState").state
    Check ($st.foreground.phase -eq "paused") "al reanudar la salida el video NO se reanuda solo"
    Send-Command $cws "media.resume" @{ target = "foreground" }
    Start-Sleep -Milliseconds 800
    Check (-not [bool](Eval $ows 'document.querySelector("[data-layer=foreground] video").paused')) "el operador lo reanuda"

    # ───────── 3. Buscar, volumen, fin y detener ─────────
    Write-Host "Buscar y volumen"
    Send-Command $cws "media.seek" @{ target = "foreground"; positionMs = 4000 }
    Start-Sleep -Milliseconds 700
    $tseek = [double](Eval $ows 'document.querySelector("[data-layer=foreground] video").currentTime')
    Check ($tseek -ge 3.8 -and $tseek -le 5.6) "buscar a 4 s: el video esta en $([math]::Round($tseek,2)) s"
    Send-Command $cws "media.setVolume" @{ target = "foreground"; volumePct = 40; muted = $null }
    Start-Sleep -Milliseconds 700
    $vol = [double](Eval $ows 'document.querySelector("[data-layer=foreground] video").volume')
    Check ([Math]::Abs($vol - 0.10) -lt 0.03) "volumen del clip 40 % x maestro 25 % = 0,10 ($vol)"
    Send-Command $cws "media.setVolume" @{ target = "foreground"; volumePct = $null; muted = $true }
    Start-Sleep -Milliseconds 400
    Check ([bool](Eval $ows 'document.querySelector("[data-layer=foreground] video").muted')) "silenciar el video"
    Send-Command $cws "media.setVolume" @{ target = "foreground"; volumePct = $null; muted = $false }

    Write-Host "Fin del video (quedarse)"
    Send-Command $cws "media.play" @{ mediaId = $video.id; startPlaying = $true; volumePct = 100; muted = $true; loop = $false; fadeInMs = 0; fadeOutMs = 100; backdrop = "black"; onEnd = "stay" }
    Start-Sleep -Milliseconds 1500
    Send-Command $cws "media.seek" @{ target = "foreground"; positionMs = 5000 }
    $end = [DateTime]::UtcNow.AddSeconds(12); $phase = ""
    while ([DateTime]::UtcNow -lt $end -and $phase -ne "ended") { Start-Sleep -Milliseconds 500; $phase = (Ask $cws "output.getState").state.foreground.phase }
    Check ($phase -eq "ended") "al terminar con «quedarse» el estado dice 'ended' (queda en su ultimo fotograma)"
    Check ([bool](Eval $ows 'document.querySelector("[data-layer=foreground] video").paused')) "y el video esta pausado"
    Send-Command $cws "media.stop" @{ target = "foreground" }
    Start-Sleep -Milliseconds 1500
    $gone = Eval $ows 'JSON.stringify({ videos: document.querySelectorAll("[data-layer=foreground] video").length, bgSame: document.querySelector("[data-layer=background] video") === window.__bgNode, bgPaused: document.querySelector("[data-layer=background] video").paused, detached: !window.__fgNode.isConnected, noSrc: !window.__fgNode.hasAttribute("src") })' | ConvertFrom-Json
    Check ($gone.videos -eq 0 -and $gone.detached -and $gone.noSrc) "detener libera el elemento DE VERDAD (fuera del DOM y sin src)"
    Check ($gone.bgSame -and -not $gone.bgPaused) "el fondo sigue intacto (mismo nodo, reproduciendose)"

    Write-Host "Fin del video (volver a lo anterior)"
    Send-Command $cws "media.play" @{ mediaId = $video.id; startPlaying = $true; volumePct = 100; muted = $true; loop = $false; fadeInMs = 0; fadeOutMs = 100; backdrop = "background"; onEnd = "back" }
    Start-Sleep -Milliseconds 1500
    Send-Command $cws "media.seek" @{ target = "foreground"; positionMs = 5300 }
    $end = [DateTime]::UtcNow.AddSeconds(10); $fgNull = $false
    while ([DateTime]::UtcNow -lt $end -and -not $fgNull) { Start-Sleep -Milliseconds 400; $fgNull = ($null -eq (Ask $cws "output.getState").state.foreground) }
    Check $fgNull "al terminar con «volver» el video sale del estado y se descubre lo de debajo"

    # ───────── 4. Pistas de audio ─────────
    Write-Host "Pistas de audio (motor de C#, NAudio)"
    $a0 = Ask $cws "audio.getState"
    Check ($a0.devices.Count -ge 1) "hay $($a0.devices.Count) dispositivo(s) de salida activo(s)"
    Check ($a0.route.usesWindowsDefault -and -not $a0.route.mismatch) "sin dispositivo de la iglesia: las pistas usan el predeterminado de Windows y no hay discrepancia"
    Send-Command $cws "media.play" @{ mediaId = $wav.id; startPlaying = $false; volumePct = $null; muted = $false; loop = $false; fadeInMs = $null; fadeOutMs = $null; backdrop = "black"; onEnd = "back" }
    Start-Sleep -Seconds 1
    $a1 = Ask $cws "audio.getState"
    Check ($a1.track.phase -eq "paused") "un audio se carga LISTO en pausa: no suena sin pedirlo"
    Check ((Get-PeakFor 500) -lt 0.001) "y de verdad no suena (pico ~0)"
    Send-Command $cws "media.resume" @{ target = "track" }
    Start-Sleep -Milliseconds 1500
    $a2 = Ask $cws "audio.getState"
    Check ($a2.track.phase -eq "playing") "reproducir: la pista suena"
    $peak = Get-PeakFor 1000
    $result.peakTrack = $peak
    Check ($peak -gt 0.01) "SE OYE la pista: pico del dispositivo $([math]::Round($peak, 4))"
    Send-Command $cws "output.setMaster" @{ master = "black" }
    Send-Command $cws "output.recover" @{ reason = "manual" }
    Start-Sleep -Milliseconds 1500
    $a3 = Ask $cws "audio.getState"
    Check ($a3.track.phase -eq "playing") "la pista NO se corta con Negro ni con la pausa de la salida"
    Check ((Get-PeakFor 800) -gt 0.01) "y sigue sonando de verdad"
    Send-Command $cws "output.open" @{ displayId = $panelId }
    Send-Command $cws "output.setMaster" @{ master = "live" }
    Start-Sleep -Milliseconds 800
    Send-Command $cws "media.seek" @{ target = "track"; positionMs = 3000 }
    Start-Sleep -Milliseconds 600
    $a4 = Ask $cws "audio.getState"
    Check ($a4.track.positionMs -ge 2500 -and $a4.track.positionMs -le 4500) "buscar la pista a 3 s ($($a4.track.positionMs) ms)"
    Send-Command $cws "media.pause" @{ target = "track" }
    Start-Sleep -Milliseconds 1500
    Check ((Ask $cws "audio.getState").track.phase -eq "paused") "pausar la pista"
    Check ((Get-PeakFor 600) -lt 0.001) "y calla de verdad (pico ~0)"

    # ───────── 5. «Silencio» con la tecla REAL ─────────
    Write-Host "Silencio (Ctrl+Alt+Shift+S, tecla real)"
    Send-Command $cws "media.resume" @{ target = "track" }
    Send-Command $cws "media.play" @{ mediaId = $video.id; startPlaying = $true; volumePct = 100; muted = $false; loop = $true; fadeInMs = 0; fadeOutMs = 0; backdrop = "black"; onEnd = "back" }
    Start-Sleep -Milliseconds 2000
    Check ((Get-PeakFor 600) -gt 0.01) "antes del Silencio hay senal en el dispositivo"
    Press-Hotkey 0x53
    Start-Sleep -Milliseconds 900
    $a5 = Ask $cws "audio.getState"
    $fgSil = Eval $ows 'document.querySelector("[data-layer=foreground] video").paused'
    Check ($a5.track.phase -eq "paused") "Silencio: la pista queda EN PAUSA (cargada, con su posicion)"
    Check ([bool]$fgSil) "Silencio: el video en primer plano queda pausado"
    Check ((Get-PeakFor 800) -lt 0.001) "Silencio: el pico del dispositivo cae a ~0"
    Press-Hotkey 0x53
    Start-Sleep -Milliseconds 800
    Check ((Ask $cws "audio.getState").track.phase -eq "paused") "un segundo pulso NO reanuda nada (no es un interruptor)"
    Check ((Get-PeakFor 500) -lt 0.001) "y sigue en silencio"
    Send-Command $cws "media.stopAll" @{}   # el mismo mensaje que el respaldo de la pagina de salida
    Send-Command $cws "media.stop" @{ target = "foreground" }
    Send-Command $cws "media.stop" @{ target = "track" }

    # ───────── 6. Tono de prueba y ruta ─────────
    Write-Host "Probar sonido y ruta de audio"
    Send-Command $cws "audio.test" @{ target = "default" }
    $pt = Get-PeakFor 1100
    $result.peakTestDefault = $pt
    Check ($pt -gt 0.005) "«Probar sonido» por el predeterminado de Windows: se OYE (pico $([math]::Round($pt, 4)))"
    Start-Sleep -Milliseconds 800
    Send-Command $cws "audio.test" @{ target = "church" }
    $pt2 = Get-PeakFor 1100
    Check ($pt2 -gt 0.005) "«Probar sonido» del dispositivo de la iglesia (sin uno elegido = el predeterminado): se OYE (pico $([math]::Round($pt2, 4)))"
    $others = @($a0.devices | Where-Object { -not $_.isDefault })
    if ($others.Count -ge 1) {
        $other = $others[0]
        Send-Command $cws "audio.setDevice" @{ deviceId = $other.id }
        Start-Sleep -Milliseconds 700
        $r1 = Ask $cws "audio.getState"
        Check ($r1.route.mismatch -and $r1.route.churchDeviceId -eq $other.id) "con un dispositivo de la iglesia distinto del predeterminado de Windows: la ruta marca la DISCREPANCIA"
        $uiWarn = [bool](Eval $cws '!![...document.querySelectorAll("[data-testid=player-warning]")].find((n) => /predeterminado de Windows/.test(n.textContent))')
        Check $uiWarn "y el panel la muestra con un aviso visible"
        Send-Command $cws "audio.test" @{ target = "church" }
        $ptc = Get-PeakFor 1200 ([string]$other.id)
        Check ($ptc -gt 0.005) "el tono del dispositivo de la iglesia suena en ESE dispositivo (pico $([math]::Round($ptc, 4)))"
        Send-Command $cws "audio.setDevice" @{ deviceId = $null }
    } else { Write-Host "  (solo hay un dispositivo de salida activo: no se prueba la discrepancia con uno real; la cubren los tests con el enumerador simulado)" }
    Check ($defaultBefore -eq [PeakMeter]::DefaultId()) "la aplicacion NO cambio el dispositivo predeterminado de Windows"

    # ───────── 7. El panel no se atasca y el registro no lleva nombres ─────────
    $logs = Log-Text
    Check ($logs -notmatch [regex]::Escape($SECRET)) "el registro de la aplicacion NO contiene nombres ni rutas del operador"
    Check ($logs -match "Pista \d+ ") "(el registro si anota las pistas por id)"
    $result.hotkeys = (Ask $cws "output.getState").window.hotkeys | ConvertTo-Json -Compress
    Check ([bool](((Ask $cws "output.getState").window.hotkeys | Where-Object { $_.action -eq "silence" -and $_.registered }))) "el atajo global de Silencio esta registrado"
}
finally {
    try { if (-not $proc.HasExited) { $proc.CloseMainWindow() | Out-Null; if (-not $proc.WaitForExit(6000)) { $proc.Kill() } } } catch { }
    Get-Process -Name OakProyektor.App -ErrorAction SilentlyContinue | Where-Object { $_.Id -eq $proc.Id } | Stop-Process -Force -ErrorAction SilentlyContinue
    if (-not $KeepData) { Start-Sleep -Seconds 1; Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
}
$result | ConvertTo-Json -Depth 5
if ($fails.Count -gt 0) { Write-Host "`nFALLARON $($fails.Count) comprobaciones:" -ForegroundColor Red; $fails | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }; exit 1 } else { Write-Host "`nTodo correcto." -ForegroundColor Green }
