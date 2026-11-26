# Pruebas del motor multimedia de la salida, sobre la APLICACION REAL (compilacion Debug) con la depuracion remota de WebView2 SOLO para esta prueba.
# La salida se abre en una VENTANA NORMAL en el monitor del panel (nunca en el proyector). Los datos van a una carpeta temporal (OAK_DATA_DIR).
#
#   -Mode switch : cambia el fondo -Changes veces (por defecto 200) alternando video A, video B, imagen y color, y mide que la memoria del navegador de la
#                  salida no crezca y que no queden <video> sin liberar.
#   -Mode gap    : mide el salto del bucle y cualquier hueco de presentacion del video (p. ej. el de ~666 ms que se vio en los MP4 generados): lista cada intervalo
#                  entre fotogramas muy superior a la mediana, DONDE ocurre (mediaTime) y si es el reinicio del bucle.
#   -Mode checks : integracion extremo a extremo con la app real: rutas rechazadas (red, inexistente, extension), un archivo corrupto (fallo de reproduccion ->
#                  color de reserva + aviso visible en el panel) y la PAUSA/REANUDACION de la salida (el video sigue: mismo nodo, currentTime continua).
#   -Mode soak   : prueba de resistencia (-Minutes, 60 por defecto): video en bucle + un cambio de texto cada -TextEverySeconds; muestrea cada 30 s memoria,
#                  nodo de video (mismo nodo), reinicios inesperados del video, fotogramas perdidos y huecos. Escribe un CSV.
#
# Con -Video <ruta.mp4> (uno o dos MP4 H.264 REALES tuyos) usa tus archivos; sin -Video genera material de prueba con MediaRecorder dentro de la propia salida
# (se guarda en la carpeta temporal; no se versiona nada). Ejemplos:
#   powershell -File tools\media-test.ps1 -Mode switch
#   powershell -File tools\media-test.ps1 -Mode gap  -Video "D:\Fondos\culto.mp4"
#   powershell -File tools\media-test.ps1 -Mode soak -Video "D:\Fondos\culto.mp4" -Minutes 60
param(
    [ValidateSet("switch", "gap", "soak", "checks")][string]$Mode = "switch",
    [string[]]$Video = @(),
    [int]$Changes = 200,
    [int]$Minutes = 60,
    [int]$TextEverySeconds = 4,
    [string]$Out = "",
    [switch]$KeepData        # no borra la carpeta temporal (para revisar los logs de la aplicacion)
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
if (-not $exe) { throw "Compila en Debug primero (npm run build en frontend/ y dotnet build)." }

$data = Join-Path $env:TEMP ("oak-media-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $data | Out-Null
$mediaDir = Join-Path $data "media-test"; New-Item -ItemType Directory $mediaDir | Out-Null
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
    $r = Invoke-Cdp $ws "Runtime.evaluate" @{ expression = $expr; awaitPromise = $true; returnByValue = $true } $timeoutMs
    if ($null -eq $r) { return $null }
    if ($r.result.exceptionDetails) { throw ("Error en la pagina: " + ($r.result.exceptionDetails | ConvertTo-Json -Depth 4 -Compress)) }
    return $r.result.result.value
}
function Send-Command($ws, [string]$type, $payload) {
    $json = $payload | ConvertTo-Json -Depth 8 -Compress
    [void](Eval $ws ("window.external.sendMessage(JSON.stringify({v:15,id:crypto.randomUUID(),kind:'command',type:'$type',payload:$json}));true"))
}

# Memoria PRIVADA de los procesos del navegador de la SALIDA (su perfil esta en la linea de comandos), por tipo.
function Get-OutputMemory {
    $match = [regex]::Escape((Join-Path $data "cache\WebView2-output"))
    $by = @{ renderer = 0.0; gpu = 0.0; browser = 0.0; other = 0.0 }
    Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" | Where-Object { $_.CommandLine -match $match } | ForEach-Object {
        $p = Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue; if (-not $p) { return }
        $mb = $p.PrivateMemorySize64 / 1MB
        $type = if ($_.CommandLine -match '--type=renderer') { "renderer" } elseif ($_.CommandLine -match '--type=gpu-process') { "gpu" } elseif ($_.CommandLine -notmatch '--type=') { "browser" } else { "other" }
        $by[$type] += $mb
    }
    [pscustomobject]@{ renderer = [math]::Round($by.renderer, 1); gpu = [math]::Round($by.gpu, 1); browser = [math]::Round($by.browser, 1); other = [math]::Round($by.other, 1); total = [math]::Round(($by.renderer + $by.gpu + $by.browser + $by.other), 1) }
}

$readState = 'JSON.stringify({ videos: document.querySelectorAll("video").length, imgs: document.querySelectorAll("img").length, heapMB: Math.round((performance.memory ? performance.memory.usedJSHeapSize : 0) / 1048576 * 10) / 10 })'

# Genera un MP4 H.264 con MediaRecorder DENTRO de la salida (sin ffmpeg) y lo guarda en $path. Solo es material de PRUEBA (no se versiona).
function New-TestVideo($ws, [string]$path, [int]$seconds, [int]$hueBase) {
    $js = @"
(async () => {
  const mime = 'video/mp4;codecs=avc1.640028';
  if (!MediaRecorder.isTypeSupported(mime)) return 'UNSUPPORTED';
  const c = document.createElement('canvas'); c.width = 1920; c.height = 1080; document.body.appendChild(c);
  const g = c.getContext('2d'); let f = 0, run = true;
  (function draw() { if (!run) return; f++;
    g.fillStyle = 'hsl(' + (($hueBase + f * 3) % 360) + ',60%,35%)'; g.fillRect(0, 0, 1920, 1080);
    for (let i = 0; i < 30; i++) { g.fillStyle = 'hsla(' + (($hueBase + f + i * 11) % 360) + ',80%,60%,.6)'; g.fillRect((f * (5 + i) + i * 97) % 1920, (i * 33) % 1080, 200, 80); }
    g.fillStyle = '#fff'; g.font = 'bold 180px sans-serif'; g.fillText('F' + String(f).padStart(4, '0'), 120, 540);
    requestAnimationFrame(draw); })();
  const rec = new MediaRecorder(c.captureStream(30), { mimeType: mime, videoBitsPerSecond: 8e6 });
  const chunks = []; rec.ondataavailable = (e) => e.data.size && chunks.push(e.data);
  const stopped = new Promise((r) => { rec.onstop = r; });
  rec.start(1000); await new Promise((r) => setTimeout(r, $seconds * 1000)); rec.stop(); await stopped; run = false; c.remove();
  const blob = new Blob(chunks);
  return await new Promise((resolve) => { const fr = new FileReader(); fr.onload = () => resolve(String(fr.result).split(',')[1]); fr.readAsDataURL(blob); });
})()
"@
    $b64 = Eval $ws $js (($seconds + 40) * 1000)
    if (-not $b64 -or $b64 -eq "UNSUPPORTED") { throw "No se pudo generar el MP4 de prueba (MediaRecorder sin H.264). Usa -Video con tus archivos." }
    [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($b64))
}
function New-TestImage($ws, [string]$path) {
    $b64 = Eval $ws "(async () => { const c = document.createElement('canvas'); c.width = 1920; c.height = 1080; const g = c.getContext('2d'); const gr = g.createLinearGradient(0,0,1920,1080); gr.addColorStop(0,'#134e4a'); gr.addColorStop(1,'#7c2d12'); g.fillStyle = gr; g.fillRect(0,0,1920,1080); return c.toDataURL('image/png').split(',')[1]; })()" 20000
    [IO.File]::WriteAllBytes($path, [Convert]::FromBase64String($b64))
}

$result = [ordered]@{}
try {
    # 1. Panel y salida (ventana normal en el monitor del panel)
    $control = $null
    for ($i = 0; $i -lt 40 -and -not $control; $i++) { Start-Sleep -Milliseconds 500; $control = Get-Targets "control" | Where-Object { $_.type -eq "page" -and $_.url -match "index\.html" } | Select-Object -First 1 }
    if (-not $control) { throw "No aparecio el panel" }
    $cws = Connect-Cdp $control.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $list = Eval $cws '(async () => { const id = crypto.randomUUID(); return await new Promise((resolve) => { window.external.receiveMessage((raw) => { const m = JSON.parse(raw); if (m.id === id) resolve(JSON.stringify(m.payload)); }); window.external.sendMessage(JSON.stringify({ v:15, id, kind: "query", type: "display.list", payload: {} })); }); })()'
    $panelId = ((($list | ConvertFrom-Json).result.displays) | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    if (-not $panelId) { throw "No se pudo leer el monitor del panel: $list" }
    [void](Eval $cws ("window.external.sendMessage(JSON.stringify({v:15,id:crypto.randomUUID(),kind:'command',type:'output.open',payload:{displayId:" + ($panelId | ConvertTo-Json -Compress) + "}}));true"))
    $output = $null
    for ($i = 0; $i -lt 40 -and -not $output; $i++) { Start-Sleep -Milliseconds 500; $output = Get-Targets "output" | Where-Object { $_.type -eq "page" -and $_.url -match "output\.html" } | Select-Object -First 1 }
    if (-not $output) { throw "No aparecio la ventana de salida" }
    $ows = Connect-Cdp $output.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $result.chromium = Eval $ows "navigator.userAgent"

    # 2. Material
    $videos = @(); foreach ($v in $Video) { if (-not (Test-Path $v)) { throw "No existe $v" }; $videos += (Resolve-Path $v).Path }
    $generated = $false
    if ($videos.Count -eq 0) {
        $generated = $true
        $a = Join-Path $mediaDir "a.mp4"; $b = Join-Path $mediaDir "b.mp4"
        Write-Host "Generando dos MP4 H.264 de prueba (8 s cada uno)..."
        New-TestVideo $ows $a 8 20; New-TestVideo $ows $b 8 200
        $videos = @($a, $b)
    }
    if ($videos.Count -eq 1) { $videos += $videos[0] }
    $result.video = ($videos | Select-Object -First 2) -join " | "
    $image = Join-Path $mediaDir "i.png"; New-TestImage $ows $image

    $bgVideo = @{ kind = "video"; path = $videos[0]; color = "#102030"; muted = $true; transitionMs = 300 }   # (no llamarlo $video: PowerShell no distingue mayusculas y es el parametro -Video)
    Send-Command $cws "output.setMaster" @{ master = "live" }
    Send-Command $cws "output.setBackground" $bgVideo
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) { Start-Sleep -Milliseconds 250; $ready = [bool](Eval $ows '(() => { const v = document.querySelector("video"); return !!v && v.readyState >= 2 && v.currentTime > 0; })()') }
    $alert = Eval $cws '(document.querySelector("[role=alert]") || {}).innerText || ""'
    if (-not $ready) { throw "El video no empezo a reproducirse en la salida. Aviso del panel: $alert" }
    $result.videoPlaying = $true
    $result.videoInfo = Eval $ows 'JSON.stringify((() => { const v = document.querySelector("video"); return { w: v.videoWidth, h: v.videoHeight, duration: v.duration, src: v.currentSrc.replace(/[^/]*$/, "...") }; })())'

    # ---------------------------------------------------------------- switch
    if ($Mode -eq "switch") {
        $cycle = @(
            @{ kind = "video"; path = $videos[0]; color = "#102030"; muted = $true; transitionMs = 150 },
            @{ kind = "video"; path = $videos[1]; color = "#301020"; muted = $true; transitionMs = 150 },
            @{ kind = "image"; path = $image; color = "#203010"; muted = $true; transitionMs = 150 },
            @{ kind = "color"; path = $null; color = "#445566"; muted = $true; transitionMs = 150 }
        )
        for ($i = 0; $i -lt 8; $i++) { Send-Command $cws "output.setBackground" $cycle[$i % 4]; Start-Sleep -Milliseconds 500 }   # calentamiento: cada tipo se carga al menos una vez
        Start-Sleep -Seconds 3
        $m0 = Get-OutputMemory; $s0 = (Eval $ows $readState) | ConvertFrom-Json
        $samples = @(); $maxVideos = 0
        for ($i = 1; $i -le $Changes; $i++) {
            Send-Command $cws "output.setBackground" $cycle[$i % 4]
            Start-Sleep -Milliseconds 140
            if ($i % 25 -eq 0) {
                $mem = Get-OutputMemory; $st = (Eval $ows $readState) | ConvertFrom-Json
                $maxVideos = [math]::Max($maxVideos, $st.videos)
                $samples += [pscustomobject]@{ change = $i; totalMB = $mem.total; rendererMB = $mem.renderer; gpuMB = $mem.gpu; videos = $st.videos; imgs = $st.imgs; jsHeapMB = $st.heapMB }
                Write-Host ("  cambio {0,3}: total {1} MB (renderer {2}, gpu {3}) | <video> en el DOM: {4} | heap JS {5} MB" -f $i, $mem.total, $mem.renderer, $mem.gpu, $st.videos, $st.heapMB)
            }
        }
        Send-Command $cws "output.setBackground" $bgVideo
        Start-Sleep -Seconds 8
        $m1 = Get-OutputMemory; $s1 = (Eval $ows $readState) | ConvertFrom-Json
        $growth = [math]::Round($m1.total - $m0.total, 1)
        $result.memoryBeforeMB = "$($m0.total) (renderer $($m0.renderer), gpu $($m0.gpu), browser $($m0.browser))"
        $result.memoryAfterMB = "$($m1.total) (renderer $($m1.renderer), gpu $($m1.gpu), browser $($m1.browser))"
        $result.growthMB = $growth
        $result.videoElementsAtEnd = $s1.videos
        $result.maxVideoElementsSeen = $maxVideos
        $result.jsHeapMB = "$($s0.heapMB) -> $($s1.heapMB)"
        $result.errorBanner = Eval $cws '(document.querySelector("[role=alert]") || {}).innerText || ""'
        $ok = ($growth -lt 60) -and ($s1.videos -eq 1) -and ($maxVideos -le 4)
        $result.VERDICT = if ($ok) { "OK: la memoria no crece de forma apreciable y los video se liberan (umbral: +60 MB, 1 video al final, <= 4 a la vez)" } else { "REVISAR: crecimiento $growth MB, videos al final $($s1.videos), maximo simultaneo $maxVideos" }
    }

    # ---------------------------------------------------------------- checks
    if ($Mode -eq "checks") {
        $banner = '(document.querySelector("[role=alert]") || {}).innerText || ""'
        $dismiss = '[...document.querySelectorAll("button")].filter((b) => b.innerText.includes("Cerrar aviso")).forEach((b) => b.click()); true'
        $bad = [ordered]@{
            "ruta de red (UNC)" = "\\servidor\recurso\fondo.mp4"
            "archivo inexistente" = (Join-Path $mediaDir "no-existe.mp4")
            "extension no permitida" = (Join-Path $mediaDir "fondo.mkv")
            "ruta relativa" = "fondo.mp4"
        }
        [IO.File]::WriteAllBytes((Join-Path $mediaDir "fondo.mkv"), [byte[]](1..64))
        foreach ($name in $bad.Keys) {
            [void](Eval $cws $dismiss)
            Send-Command $cws "output.setBackground" @{ kind = "video"; path = $bad[$name]; color = "#102030"; muted = $true; transitionMs = 200 }
            Start-Sleep -Milliseconds 700
            $result["rechazo: $name"] = ((Eval $cws $banner) -replace "\s+", " ")
        }
        $still = Eval $ows 'document.querySelectorAll("video").length + " video(s); src=" + (document.querySelector("video") ? document.querySelector("video").getAttribute("src").slice(-12) : "-")'
        $result["tras los rechazos la salida sigue con el video anterior"] = $still

        # Archivo corrupto con extension valida: el backend lo acepta (existe, .mp4) y es la SALIDA la que no puede reproducirlo.
        $corrupt = Join-Path $mediaDir "corrupto.mp4"
        $rnd = New-Object byte[] 60000; (New-Object Random 7).NextBytes($rnd); [IO.File]::WriteAllBytes($corrupt, $rnd)
        [void](Eval $cws $dismiss)
        Send-Command $cws "output.setBackground" @{ kind = "video"; path = $corrupt; color = "#445566"; muted = $true; transitionMs = 300 }
        $failBanner = ""
        for ($i = 0; $i -lt 40 -and $failBanner -notmatch "Fallo del fondo"; $i++) { Start-Sleep -Milliseconds 250; $failBanner = (Eval $cws $banner) -replace "\s+", " " }
        $result["fallo de reproduccion (aviso del panel)"] = $failBanner
        Start-Sleep -Milliseconds 800
        $result["salida tras el fallo"] = Eval $ows 'JSON.stringify({ videos: document.querySelectorAll("video").length, baseColor: getComputedStyle(document.querySelector("[data-layer=background]").firstElementChild).backgroundColor, textLayerVisible: getComputedStyle(document.querySelector("[data-layer=content]")).visibility })'

        # H2: reintentar el MISMO archivo tras un fallo (simula el USB que se desconecta y vuelve). El archivo sigue roto: «Reintentar» lo intenta y avisa de nuevo.
        $retryClick = '[...document.querySelectorAll("button")].filter((b) => b.innerText.trim() === "Reintentar").forEach((b) => b.click()); true'
        [void](Eval $cws $retryClick)
        Start-Sleep -Milliseconds 400
        $again = ""
        for ($i = 0; $i -lt 40 -and $again -notmatch "Fallo del fondo"; $i++) { Start-Sleep -Milliseconds 250; $again = (Eval $cws $banner) -replace "\s+", " " }
        $result["reintento con el archivo todavia roto: el aviso vuelve"] = $again
        # El archivo vuelve a estar bien (se reconecto el USB): reaplicar el mismo archivo ahora SI lo reproduce y el aviso desaparece.
        Copy-Item -LiteralPath $bgVideo.path -Destination $corrupt -Force
        Send-Command $cws "output.setBackground" @{ kind = "video"; path = $corrupt; color = "#445566"; muted = $true; transitionMs = 300 }
        $played = $false
        for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 250; if ([bool](Eval $ows '(() => { const v = document.querySelector("video"); return !!v && v.getAttribute("src").endsWith("corrupto.mp4") && v.readyState >= 2 && v.currentTime > 0.3; })()')) { $played = $true; break } }
        $result["reaplicar el mismo archivo ya sano: se reproduce = $played; aviso del panel"] = ((Eval $cws $banner) -replace "\s+", " ")
        $result.retryVerdict = if ($again -match "Fallo del fondo" -and $played) { "OK: Reintentar vuelve a fallar con el archivo roto y reaplicar el mismo archivo sano lo reproduce" } else { "REVISAR el reintento (aviso de nuevo = $($again -match 'Fallo del fondo'); reproduce = $played)" }
        # Pausa y reanudacion de la salida con un video reproduciendose.
        [void](Eval $cws $dismiss)
        Send-Command $cws "output.setBackground" $bgVideo
        Start-Sleep -Seconds 2   # deja terminar el fundido: el video anterior sigue en el DOM mientras sale y no debe confundirse con el nuevo
        for ($i = 0; $i -lt 40; $i++) { Start-Sleep -Milliseconds 250; if ([bool](Eval $ows '(() => { const v = document.querySelector("video"); return !!v && v.readyState >= 2 && v.currentTime > 0.5; })()')) { break } }
        [void](Eval $ows '(() => { const v = document.querySelector("video"); v.__oakNode = true; window.__oakLoops = 0; v.addEventListener("seeked", () => { window.__oakLoops++; }); return true; })()')
        $before = Eval $ows 'JSON.stringify((() => { const v = document.querySelector("video"); return { t: +v.currentTime.toFixed(2), paused: v.paused, hidden: document.hidden }; })())'
        Send-Command $cws "output.recover" @{ reason = "manual" }
        Start-Sleep -Seconds 4
        $during = Eval $ows 'JSON.stringify((() => { const v = document.querySelector("video"); return { t: +v.currentTime.toFixed(2), paused: v.paused, hidden: document.hidden }; })())'
        $paused = Eval $cws 'document.body.innerText.includes("SALIDA EN PAUSA")'
        Send-Command $cws "output.open" @{ displayId = $panelId }
        Start-Sleep -Seconds 3
        $after = Eval $ows 'JSON.stringify((() => { const v = document.querySelector("video"); return { t: +v.currentTime.toFixed(2), paused: v.paused, hidden: document.hidden, sameNode: v.__oakNode === true, videos: document.querySelectorAll("video").length }; })())'
        $result["pausa: antes"] = $before
        $result["pausa: durante (4 s minimizada; el panel dice EN PAUSA = $paused)"] = $during
        $result["pausa: despues de reanudar (3 s)"] = $after
        $a = $after | ConvertFrom-Json
        $result.VERDICT = if ($a.sameNode -and -not $a.paused -and $a.videos -eq 1) { "OK: tras la pausa el video es el mismo nodo y esta reproduciendose (no se reinicio ni se perdio el fondo)" } else { "REVISAR la pausa/reanudacion" }
        # H1: cambiar el fondo con la salida EN PAUSA, esperar mas que el limite de carga (10 s) y reanudar: el video nuevo debe verse (no el color de reserva ni un falso fallo).
        [void](Eval $cws $dismiss)
        Send-Command $cws "output.recover" @{ reason = "manual" }
        Start-Sleep -Seconds 2
        Send-Command $cws "output.setBackground" @{ kind = "video"; path = $videos[1]; color = "#102030"; muted = $true; transitionMs = 300 }
        Start-Sleep -Seconds 13
        Send-Command $cws "output.open" @{ displayId = $panelId }
        $okPause = $false
        for ($i = 0; $i -lt 60; $i++) { Start-Sleep -Milliseconds 250; if ([bool](Eval $ows '(() => { const v = document.querySelector("video"); return !!v && v.getAttribute("src").endsWith("b.mp4") && v.readyState >= 2 && v.currentTime > 0.3 && !v.paused; })()')) { $okPause = $true; break } }
        $result["H1 fondo cambiado en pausa (13 s) y reanudado: b.mp4 se reproduce = $okPause; aviso del panel"] = ((Eval $cws $banner) -replace "\s+", " ")
        $result.pauseBackgroundVerdict = if ($okPause) { "OK: el fondo cambiado durante la pausa se ve al reanudar, sin falso fallo" } else { "REVISAR: el fondo cambiado en pausa no se reproduce al reanudar" }
    }

    # ---------------------------------------------------------------- gap
    if ($Mode -eq "gap") {
        $loops = 3
        $dur = [double](Eval $ows 'document.querySelector("video").duration')
        $secs = [int]([math]::Ceiling($dur * $loops) + 3)
        $js = @"
new Promise((resolve) => {
  const v = document.querySelector('video'); const rows = []; let stop = false;
  const cb = (now, md) => { rows.push([now, md.mediaTime, md.presentedFrames]); if (!stop) v.requestVideoFrameCallback(cb); };
  v.requestVideoFrameCallback(cb);
  setTimeout(() => { stop = true; const q = v.getVideoPlaybackQuality();
    const iv = []; for (let i = 1; i < rows.length; i++) iv.push(rows[i][0] - rows[i - 1][0]);
    const sorted = [...iv].sort((a, b) => a - b); const median = sorted[sorted.length >> 1];
    const gaps = []; for (let i = 1; i < rows.length; i++) { const dt = rows[i][0] - rows[i - 1][0];
      if (dt > Math.max(3 * median, 100)) gaps.push({ gapMs: Math.round(dt), fromMedia: +rows[i - 1][1].toFixed(3), toMedia: +rows[i][1].toFixed(3), wrap: rows[i][1] < rows[i - 1][1] - 0.5, missed: rows[i][2] - rows[i - 1][2] - 1 }); }
    const wraps = []; for (let i = 1; i < rows.length; i++) if (rows[i][1] < rows[i - 1][1] - 0.5) wraps.push({ gapMs: Math.round(rows[i][0] - rows[i - 1][0]), missed: rows[i][2] - rows[i - 1][2] - 1, fromMedia: +rows[i - 1][1].toFixed(3), toMedia: +rows[i][1].toFixed(3) });
    resolve(JSON.stringify({ frames: rows.length, medianFrameMs: +median.toFixed(1), p99FrameMs: +sorted[Math.floor(sorted.length * .99)].toFixed(1), dropped: q.droppedVideoFrames, total: q.totalVideoFrames, loopWraps: wraps, gapsOver100msOr3xMedian: gaps }));
  }, $secs * 1000);
})
"@
        Write-Host "Midiendo $secs s de reproduccion ($loops vueltas) ..."
        $g = (Eval $ows $js (($secs + 30) * 1000)) | ConvertFrom-Json
        $result.durationS = [math]::Round($dur, 2)
        $result.frames = $g.frames; $result.medianFrameMs = $g.medianFrameMs; $result.p99FrameMs = $g.p99FrameMs
        $result.droppedFrames = "$($g.dropped) de $($g.total)"
        $result.loopWraps = ($g.loopWraps | ForEach-Object { "gap $($_.gapMs) ms, fotogramas perdidos $($_.missed) ($($_.fromMedia)s -> $($_.toMedia)s)" }) -join " | "
        $nonWrap = @($g.gapsOver100msOr3xMedian | Where-Object { -not $_.wrap })
        $result.gapsOutsideTheLoop = if ($nonWrap.Count -eq 0) { "ninguno > 100 ms ni > 3x la mediana" } else { ($nonWrap | ForEach-Object { "$($_.gapMs) ms en mediaTime $($_.fromMedia)s -> $($_.toMedia)s (perdidos $($_.missed))" }) -join " | " }
        $result.note = "Si hay un hueco ~666 ms FUERA del bucle: mira SI SE REPITE EN LA MISMA POSICION del video en cada vuelta (es del archivo) o si aparece en posiciones distintas (es de la reproduccion)."
    }

    # ---------------------------------------------------------------- soak
    if ($Mode -eq "soak") {
        if (-not $Out) { $dir = Join-Path $root "data"; New-Item -ItemType Directory -Force $dir | Out-Null; $Out = Join-Path $dir ("media-soak-" + (Get-Date -Format "yyyyMMdd-HHmm") + ".csv") }
        $theme = @{ fontFamily = "Arial, sans-serif"; fontSize = 72; color = "#FFFFFF"; outlineWidth = 0; outlineColor = "#000000"; shadowBlur = 8; shadowOffset = 3; shadowColor = "#000000"; align = "center"; verticalAlign = "middle"; lineHeight = 1.25; marginTop = 80; marginRight = 120; marginBottom = 80; marginLeft = 120; autoFit = $true; minFontSize = 28 }
        # Monitor DENTRO de la salida: marca el nodo de video (para comprobar que es el mismo al final) y cuenta reinicios inesperados, vueltas del bucle y huecos.
        [void](Eval $ows @'
(() => {
  const v = document.querySelector("video"); v.__oakSoakNode = true;
  const m = (window.__oakSoak = { frames: 0, wraps: 0, unexpectedResets: 0, gaps100: 0, maxGapMs: 0, last: null });
  const cb = (now, md) => {
    if (m.last) {
      const dt = now - m.last.now; m.frames++;
      if (dt > 100) m.gaps100++;
      if (dt > m.maxGapMs) m.maxGapMs = Math.round(dt);
      if (md.mediaTime < m.last.mt - 0.5) { if (m.last.mt > v.duration - 1.5) m.wraps++; else m.unexpectedResets++; }
    }
    m.last = { now, mt: md.mediaTime };
    v.requestVideoFrameCallback(cb);
  };
  v.requestVideoFrameCallback(cb);
  return true;
})()
'@)
        "t_min,total_mb,renderer_mb,gpu_mb,browser_mb,videos_in_dom,same_node,dropped,total_frames,wraps,unexpected_resets,gaps_over_100ms,max_gap_ms,js_heap_mb,text_changes,panel_alert" | Set-Content $Out -Encoding ASCII
        $t0 = Get-Date; $end = $t0.AddMinutes($Minutes); $next = $t0; $changes = 0; $nextSample = $t0
        Write-Host "Resistencia: $Minutes min, un cambio de texto cada $TextEverySeconds s. CSV: $Out (Ctrl+C para cortar)"
        $first = $null; $last = $null
        while ((Get-Date) -lt $end) {
            $now = Get-Date
            if ($now -ge $next) {
                $changes++
                Send-Command $cws "output.showContent" @{ blocks = @("Cambio de texto $changes", "Segundo bloque $($changes * 7)"); theme = $theme; transitionMs = 400 }
                $next = $now.AddSeconds($TextEverySeconds + (Get-Random -Minimum -1000 -Maximum 1000) / 1000.0)
            }
            if ($now -ge $nextSample) {
                $mem = Get-OutputMemory
                $s = (Eval $ows 'JSON.stringify((() => { const v = document.querySelector("video"); const q = v ? v.getVideoPlaybackQuality() : { droppedVideoFrames: -1, totalVideoFrames: -1 }; const m = window.__oakSoak || {}; return { videos: document.querySelectorAll("video").length, same: !!(v && v.__oakSoakNode), dropped: q.droppedVideoFrames, total: q.totalVideoFrames, wraps: m.wraps, resets: m.unexpectedResets, gaps: m.gaps100, maxGap: m.maxGapMs, heap: Math.round((performance.memory ? performance.memory.usedJSHeapSize : 0) / 1048576 * 10) / 10 }; })())') | ConvertFrom-Json
                $alert = (Eval $cws '(document.querySelector("[role=alert]") || {}).innerText || ""') -replace '[,\r\n]', ' '
                $row = "{0:N1},{1},{2},{3},{4},{5},{6},{7},{8},{9},{10},{11},{12},{13},{14},{15}" -f (($now - $t0).TotalMinutes), $mem.total, $mem.renderer, $mem.gpu, $mem.browser, $s.videos, $s.same, $s.dropped, $s.total, $s.wraps, $s.resets, $s.gaps, $s.maxGap, $s.heap, $changes, $alert
                Add-Content $Out $row -Encoding ASCII
                # Linea base = primera muestra a partir del minuto 2 (si la prueba es mas corta, la primera): asi no cuenta el calentamiento ni el material generado.
                if (-not $first -and ((($now - $t0).TotalMinutes -ge 2) -or $Minutes -lt 3)) { $first = [pscustomobject]@{ mem = $mem.total } }
                $last = [pscustomobject]@{ mem = $mem.total; s = $s }
                Write-Host ("  {0,5:N1} min | {1} MB | video en DOM {2} | mismo nodo {3} | vueltas {4} | reinicios inesperados {5} | huecos>100ms {6} (max {7} ms) | cambios {8}" -f (($now - $t0).TotalMinutes), $mem.total, $s.videos, $s.same, $s.wraps, $s.resets, $s.gaps, $s.maxGap, $changes)
                $nextSample = $now.AddSeconds(30)
            }
            Start-Sleep -Milliseconds 250
        }
        $result.csv = $Out
        $result.textChanges = $changes
        if (-not $first) { $first = [pscustomobject]@{ mem = $last.mem } }
        $result.memoryBaselineMB = "$($first.mem) (primera muestra desde el minuto 2)"
        $result.memoryLastSampleMB = $last.mem
        $result.growthSinceBaselineMB = [math]::Round($last.mem - $first.mem, 1)
        $result.sameVideoNodeAtEnd = $last.s.same
        $result.unexpectedVideoResets = $last.s.resets
        $result.gapsOver100ms = "$($last.s.gaps) (max $($last.s.maxGap) ms)"
        $result.VERDICT = if ($last.s.same -and $last.s.resets -eq 0 -and $last.s.videos -eq 1) { "El video fue el mismo nodo toda la prueba y no se reinicio. Revisa la tendencia de memoria en el CSV." } else { "REVISAR: nodo distinto, reinicios inesperados o videos de mas" }
    }
}
finally {
    $result.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }   # solo el PID que arrancamos
    Start-Sleep -Seconds 1
    if ($KeepData) { Write-Host "Datos conservados en $data (logs en $data\logs)" } else { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
}
