# Prueba de LAYOUT REAL de las alertas (cinta estatica y desplazamiento) sobre la APLICACION REAL (compilacion Debug) con la depuracion remota de WebView2 SOLO para esta prueba.
# La salida se abre en una VENTANA NORMAL en el monitor del panel (nunca en el proyector). Datos en una carpeta temporal (OAK_DATA_DIR). SOLO texto SINTETICO.
#
# Comprueba, con la salida real: cinta de 1, 2 y 3 renglones (40, 140 y 300 caracteres) sin recortar el texto y con la altura bajo el tope del 30 %; el desplazamiento (animacion
# en marcha, velocidad medida, 300 caracteres); con un VIDEO de fondo en bucle: mismo nodo, currentTime continuo y fotogramas presentados con la alerta puesta, quitada y con el
# desplazamiento; el crossfade del texto intacto; la caducidad REAL por el reloj del backend (5 s); Freeze (la alerta no se congela), Negro y Texto limpio; la pausa de la salida
# (la alerta se quita); y que el registro de la aplicacion no contenga el texto de ninguna alerta.
#   powershell -File tools\alert-layout-test.ps1 [-KeepData]
param(
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$protoV = [regex]::Match((Get-Content (Join-Path $root "frontend\src\ipc\protocol.ts") -Raw), 'PROTOCOL_VERSION\s*=\s*(\d+)').Groups[1].Value
if (-not $protoV) { throw 'No se pudo leer PROTOCOL_VERSION de frontend\src\ipc\protocol.ts' }
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
if (-not $exe) { throw "Compila en Debug primero (npm run build en frontend/ y dotnet build)." }

$data = Join-Path $env:TEMP ("oak-alert-" + [guid]::NewGuid().ToString("N"))
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
$fails = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) { if ($ok) { Write-Host "  ok    $what" } else { Write-Host "  FALLA $what" -ForegroundColor Red; $script:fails.Add($what) } }
$theme = @{ fontFamily = "Arial"; fontSize = 72; color = "#FFFFFF"; outlineWidth = 0; outlineColor = "#000000"; shadowBlur = 0; shadowOffset = 0; shadowColor = "#000000"; align = "center"; verticalAlign = "middle"; lineHeight = 1.2; marginTop = 50; marginRight = 50; marginBottom = 50; marginLeft = 50; autoFit = $true; minFontSize = 24 }
$probe = @'
(() => { const c = document.querySelector('.canvas').getBoundingClientRect(); const s = c.width / 1920; const b = document.querySelector('.alert-band');
  const ov = document.querySelector('[data-layer="overlays"]'); const v = document.querySelector('video'); const q = v ? v.getVideoPlaybackQuality() : { totalVideoFrames: -1 };
  const slots = [...document.querySelectorAll('.content-slot')].map((x) => getComputedStyle(x).opacity);
  const base = { overlaysVisible: ov ? getComputedStyle(ov).visibility : null, videoCount: document.querySelectorAll('video').length, sameVideo: !!(v && v.__oakMark), t: v ? v.currentTime : -1, frames: q.totalVideoFrames, slots: slots, now: performance.now() };
  if (!b) return JSON.stringify({ present: false, ...base });
  const br = b.getBoundingClientRect(); const t = b.querySelector('.alert-text'); const tr = t ? t.getBoundingClientRect() : null; const tk = b.querySelector('.alert-ticker-track');
  return JSON.stringify({ present: true, ...base, wRatio: br.width / c.width, hCanvas: br.height / s, bottomGap: (c.bottom - br.bottom) / s, opacity: getComputedStyle(b).opacity, chars: (t || tk).textContent.length,
    fontPx: t ? parseFloat(getComputedStyle(t).fontSize) : null, textClipped: tr ? (tr.bottom > br.bottom + 1 || tr.top < br.top - 1) : false, anim: tk ? getComputedStyle(tk).animationName : null, trackLeft: tk ? tk.getBoundingClientRect().left / s : null }); })()
'@
function Get-Probe { return (Eval $ows $probe) | ConvertFrom-Json }
function Show-Alert([string]$text, [string]$mode = "band", [int]$sec = 60) { Send-Command $cws "alert.show" @{ text = $text; mode = $mode; durationSec = $sec } }
function Wait-Band([bool]$present, [int]$ms = 4000) { $end = [DateTime]::UtcNow.AddMilliseconds($ms); while ([DateTime]::UtcNow -lt $end) { if ((Get-Probe).present -eq $present) { return $true }; Start-Sleep -Milliseconds 100 }; return $false }
$secret = "AVISO SINTETICO CONFIDENCIAL"
try {
    $control = $null
    for ($i = 0; $i -lt 40 -and -not $control; $i++) { Start-Sleep -Milliseconds 500; $control = Get-Targets "control" | Where-Object { $_.type -eq "page" -and $_.url -match "index\.html" } | Select-Object -First 1 }
    if (-not $control) { throw "No aparecio el panel" }
    $cws = Connect-Cdp $control.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $list = Eval $cws '(async () => { const id = crypto.randomUUID(); return await new Promise((resolve) => { window.external.receiveMessage((raw) => { const m = JSON.parse(raw); if (m.id === id) resolve(JSON.stringify(m.payload)); }); window.external.sendMessage(JSON.stringify({ v:@@V@@, id, kind: "query", type: "display.list", payload: {} })); }); })()'
    $panelId = ((($list | ConvertFrom-Json).result.displays) | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    if (-not $panelId) { throw "No se pudo leer el monitor del panel: $list" }
    $openCmd = "window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'output.open',payload:{displayId:" + ($panelId | ConvertTo-Json -Compress) + "}}));true"
    [void](Eval $cws $openCmd)
    $output = $null
    for ($i = 0; $i -lt 40 -and -not $output; $i++) { Start-Sleep -Milliseconds 500; $output = Get-Targets "output" | Where-Object { $_.type -eq "page" -and $_.url -match "output\.html" } | Select-Object -First 1 }
    if (-not $output) { throw "No aparecio la ventana de salida" }
    $ows = Connect-Cdp $output.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $result.chromium = Eval $ows "navigator.userAgent"
    $result.devicePixelRatio = Eval $ows "window.devicePixelRatio"

    # Video de fondo en bucle (generado en la propia salida) y un texto de fondo
    $a = Join-Path $mediaDir "a.mp4"
    Write-Host "Generando un MP4 H.264 de prueba (8 s)..."
    New-TestVideo $ows $a 8 20
    Send-Command $cws "output.setMaster" @{ master = "live" }
    Send-Command $cws "output.setBackground" @{ kind = "video"; path = $a; color = "#102030"; muted = $true; transitionMs = 0 }
    $ready = $false
    for ($i = 0; $i -lt 60 -and -not $ready; $i++) { Start-Sleep -Milliseconds 250; $ready = [bool](Eval $ows '(() => { const v = document.querySelector("video"); return !!v && v.readyState >= 2 && v.currentTime > 0; })()') }
    if (-not $ready) { throw "El video no empezo a reproducirse en la salida" }
    [void](Eval $ows '(() => { const v = document.querySelector("video"); v.__oakMark = true; window.__oakLast = 0; window.__oakWraps = 0; const tick = () => { if (v.currentTime + 0.5 < window.__oakLast) window.__oakWraps++; window.__oakLast = v.currentTime; requestAnimationFrame(tick); }; tick(); return true; })()')
    Send-Command $cws "output.showContent" @{ blocks = @("Texto de fondo estable"); theme = $theme; transitionMs = 0 }
    Start-Sleep -Milliseconds 800
    $base = Get-Probe
    Check ($base.videoCount -eq 1 -and $base.sameVideo) "hay un unico video y esta marcado"

    Write-Host "Cinta estatica"
    foreach ($n in 40, 140, 300) {
        $text = ("$secret " * 20).Substring(0, $n).TrimEnd()
        Show-Alert $text "band" 60
        [void](Wait-Band $true); Start-Sleep -Milliseconds 500
        $p = Get-Probe
        Check ($p.present -and [math]::Abs($p.wRatio - 1) -lt 0.01) "[$n car.] la cinta ocupa todo el ancho del lienzo"
        Check ($p.hCanvas -le 325 -and $p.hCanvas -ge 40) "[$n car.] altura $([math]::Round($p.hCanvas)) px (tope 324)"
        Check ([math]::Abs($p.bottomGap) -lt 1) "[$n car.] anclada abajo"
        Check ($p.chars -eq $text.Length) "[$n car.] el texto llega ENTERO a la pantalla"
        Check (-not $p.textClipped) "[$n car.] el texto no queda recortado ($($p.fontPx) px)"
        Check ($p.fontPx -ge 31 -and $p.fontPx -le 52) "[$n car.] la letra queda entre el minimo (31) y el tamano (52): $($p.fontPx)"
    }

    Write-Host "Desplazamiento"
    $ticker = ("$secret " * 20).Substring(0, 300).TrimEnd()
    Show-Alert $ticker "ticker" 60
    [void](Wait-Band $true); Start-Sleep -Milliseconds 800
    $p1 = Get-Probe; Start-Sleep -Milliseconds 1000; $p2 = Get-Probe
    Check ($p1.anim -eq "alert-marquee") "la animacion alert-marquee esta en marcha"
    $speed = ($p1.trackLeft - $p2.trackLeft) / (($p2.now - $p1.now) / 1000)
    Check ($speed -gt 180 * 0.85 -and $speed -lt 180 * 1.15) "velocidad medida $([math]::Round($speed)) px/s del lienzo (esperado 180)"
    Check ($p2.chars -eq 300) "el texto del desplazamiento llega entero"

    Write-Host "Video y crossfade"
    $steps = @(
        @{ name = "cinta"; run = { Show-Alert "AVISO SINTETICO CORTO" "band" 60 } },
        @{ name = "desplazamiento"; run = { Show-Alert $ticker "ticker" 60 } },
        @{ name = "quitada"; run = { Send-Command $cws "alert.hide" @{} } }
    )
    foreach ($step in $steps) {
        & $step.run
        $q0 = Get-Probe; Start-Sleep -Milliseconds 2500; $q1 = Get-Probe
        $fps = ($q1.frames - $q0.frames) / (($q1.now - $q0.now) / 1000)
        Check ($q1.sameVideo -and $q1.videoCount -eq 1) "[$($step.name)] mismo nodo de video"
        Check ($q1.t -gt 0) "[$($step.name)] el video avanza (currentTime $([math]::Round($q1.t,1)))"
        Check ($fps -gt 20) "[$($step.name)] fotogramas presentados: $([math]::Round($fps)) fps"
        Check (($q1.slots -join ",") -eq ($base.slots -join ",")) "[$($step.name)] el crossfade del texto no se disparo"
    }
    $wraps = [int](Eval $ows 'window.__oakWraps')
    Check ($wraps -le 3) "el video solo dio las vueltas normales de su bucle ($wraps)"
    Check ([bool](Wait-Band $false 3000)) "quitar la alerta la retira del DOM tras el fundido"

    Write-Host "Caducidad (5 s reales)"
    Show-Alert "AVISO SINTETICO CADUCA" "band" 5
    [void](Wait-Band $true); $t0 = [DateTime]::UtcNow
    Start-Sleep -Seconds 3
    Check ((Get-Probe).present) "a los 3 s sigue en pantalla"
    $gone = Wait-Band $false 6000; $elapsed = ([DateTime]::UtcNow - $t0).TotalSeconds
    Check ($gone -and $elapsed -ge 4.5 -and $elapsed -le 7.5) "caduca sola a los $([math]::Round($elapsed,1)) s (5 s + fundido)"

    Write-Host "Controles maestros"
    Show-Alert "AVISO SINTETICO MAESTROS" "band" 60; [void](Wait-Band $true); Start-Sleep -Milliseconds 400
    foreach ($m in "clearText", "black", "logo") {
        Send-Command $cws "output.setMaster" @{ master = $m }; Start-Sleep -Milliseconds 300
        Check ((Get-Probe).overlaysVisible -eq "hidden") "$m oculta la alerta"
        Send-Command $cws "output.setMaster" @{ master = "live" }; Start-Sleep -Milliseconds 300
        Check ((Get-Probe).overlaysVisible -eq "visible") "al volver a Live reaparece"
    }
    Send-Command $cws "output.setMaster" @{ master = "freeze" }; Start-Sleep -Milliseconds 300
    Send-Command $cws "output.showContent" @{ blocks = @("Texto NUEVO durante Freeze"); theme = $theme; transitionMs = 0 }
    Show-Alert "AVISO SINTETICO EN FREEZE" "band" 60; Start-Sleep -Milliseconds 700
    $txt = Eval $ows '[...document.querySelectorAll(".content-text")].map(n => n.textContent).join("|")'
    $al = Eval $ows '(document.querySelector(".alert-text") || {}).textContent'
    Check ($txt -eq "Texto de fondo estable") "Freeze: el texto sigue congelado"
    Check ($al -eq "AVISO SINTETICO EN FREEZE") "Freeze: la alerta NO se congela (se muestra)"
    Send-Command $cws "alert.hide" @{}
    Check ([bool](Wait-Band $false 3000)) "Freeze: la alerta se QUITA con Freeze activo"
    Send-Command $cws "output.setMaster" @{ master = "live" }

    Write-Host "Pausa de la salida"
    Show-Alert "AVISO SINTETICO PAUSA" "band" 60; [void](Wait-Band $true)
    Send-Command $cws "output.recover" @{ reason = "manual" }
    Start-Sleep -Milliseconds 1500
    Check ([bool](Wait-Band $false 3000)) "al pausar la salida la alerta se quita"
    [void](Eval $cws $openCmd)
    Start-Sleep -Seconds 2
    Check (-not (Get-Probe).present) "al reanudar NO reaparece"
    Check ((Get-Probe).sameVideo) "el video sigue siendo el mismo nodo tras la pausa"

    Write-Host "Registro"
    $logText = (Get-ChildItem (Join-Path $data "logs") -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
    Check ($logText.Length -gt 0) "hay registro de la aplicacion"
    Check ($logText -notmatch "SINTETICO") "el registro NO contiene el texto de ninguna alerta"
    Check ($logText -match "Alerta a\d+ mostrada") "pero si los ids y conteos de las alertas"

    $result.FALLOS = if ($fails.Count -eq 0) { "ninguno" } else { $fails -join " || " }
}
finally {
    $result.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }   # solo el PID que arrancamos
    Start-Sleep -Seconds 1
    if ($KeepData) { Write-Host "Datos conservados en $data (logs en $data\logs)" } else { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($fails.Count -gt 0) { exit 1 }
