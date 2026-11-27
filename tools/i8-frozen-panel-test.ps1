# Prueba automatizada de I8 («el JS del panel se atasca»): arranca la aplicacion (compilacion Debug) con la depuracion remota de WebView2 SOLO para esta
# prueba, abre la salida en una VENTANA NORMAL (en el monitor del panel; no toca el proyector), congela el panel con `while(true){}` y mide si la salida
# sigue animandose. Repetir tras actualizar Photino.NET, Photino.Native o el runtime de WebView2 (ver CLAUDE.md).
# -Full: ademas comprueba (con el panel congelado) el latido perdido en el log, el aviso opcional en la salida, los atajos globales de respaldo
#        (con pulsaciones REALES de teclado: Ctrl+Alt+Shift+B/C/L) y la recarga del panel (Ctrl+Alt+Shift+R), y la ALERTA: se muestra con el panel congelado y se quita con la
#        pulsacion REAL de Ctrl+Alt+Shift+A (atajo global; la salida sigue animandose), y «SILENCIO» (v18): con una pista de audio y un video en primer plano sonando, la pulsacion REAL de
#        Ctrl+Alt+Shift+S los calla (medido con el medidor de picos de Windows por sesion de la aplicacion) y un segundo pulso no los reanuda; la salida sigue a ~60 fps.
# Uso: powershell -File tools\i8-frozen-panel-test.ps1 [-AfterFreeze <ruta de un script a ejecutar con el panel congelado>]
param([string]$AfterFreeze = "", [switch]$Full)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$protoV = [regex]::Match((Get-Content (Join-Path $root "frontend\src\ipc\protocol.ts") -Raw), 'PROTOCOL_VERSION\s*=\s*(\d+)').Groups[1].Value
if (-not $protoV) { throw 'No se pudo leer PROTOCOL_VERSION de frontend\src\ipc\protocol.ts' }
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
if (-not $exe) { throw "Compila en Debug primero (dotnet build)." }

$data = Join-Path $env:TEMP ("oak-i8-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $data | Out-Null
$env:OAK_DATA_DIR = $data; $env:OAK_NO_DIALOG = "1"
$env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=0"   # panel y salida son navegadores distintos: cada uno elige su puerto (se lee de DevToolsActivePort)
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
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $ws.SendAsync([ArraySegment[byte]]$bytes, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait(5000) | Out-Null
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs)
    $buffer = New-Object byte[] 65536
    while ([DateTime]::UtcNow -lt $deadline) {
        $sb = New-Object Text.StringBuilder
        do {
            $left = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds
            if ($left -le 0) { return $null }
            $cts = New-Object Threading.CancellationTokenSource($left)
            try { $r = $ws.ReceiveAsync([ArraySegment[byte]]$buffer, $cts.Token).GetAwaiter().GetResult() } catch { return $null }   # tras cancelar, el socket queda inutilizable: solo se usa para demostrar que NO responde
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
    return $r.result.result.value
}

function Get-Renderers {
    # Renderizadores de WebView2 de ESTA ejecucion (la carpeta de datos temporal esta en su linea de comandos)
    (Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape($data) -and $_.CommandLine -match "--type=renderer" } | Select-Object -ExpandProperty ProcessId | Sort-Object) -join ","
}

$result = [ordered]@{}
try {
    # 1. Esperar al panel
    $control = $null
    for ($i = 0; $i -lt 40 -and -not $control; $i++) { Start-Sleep -Milliseconds 500; $control = Get-Targets "control" | Where-Object { $_.type -eq "page" -and $_.url -match "index\.html" } | Select-Object -First 1 }
    if (-not $control) { throw "No aparecio el panel en el puerto $Port" }
    $cws = Connect-Cdp $control.webSocketDebuggerUrl
    Start-Sleep -Seconds 2

    # 2. Abrir la salida en una ventana normal: displayId = el monitor del panel (la salida nunca va a pantalla completa sobre el panel)
    $open = @'
(async () => {
  const send = (m) => window.external.sendMessage(JSON.stringify(m));
  const id = crypto.randomUUID();
  return await new Promise((resolve) => {
    window.external.receiveMessage((raw) => {
      const m = JSON.parse(raw);
      if (m.id === id) resolve(JSON.stringify(m.payload));
    });
    send({ v:@@V@@, id, kind: "query", type: "display.list", payload: {} });
  });
})()
'@
    $list = Eval $cws $open
    $result.displayList = $list
    $panelId = ((($list | ConvertFrom-Json).result.displays) | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    if (-not $panelId) { throw "No se pudo leer el monitor del panel: $list" }
    $cmd = "window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'output.open',payload:{displayId:" + ($panelId | ConvertTo-Json -Compress) + "}}));true"
    [void](Eval $cws $cmd)

    $output = $null
    for ($i = 0; $i -lt 40 -and -not $output; $i++) { Start-Sleep -Milliseconds 500; $output = Get-Targets "output" | Where-Object { $_.type -eq "page" -and $_.url -match "output\.html" } | Select-Object -First 1 }
    if (-not $output) { throw "No aparecio la ventana de salida" }
    $ows = Connect-Cdp $output.webSocketDebuggerUrl
    Start-Sleep -Seconds 2

    # 3. Animacion de prueba en la salida (CSS + rAF): se mide el avance antes y despues de congelar el panel
    [void](Eval $ows @'
(() => {
  const s = document.createElement('style');
  s.textContent = '@keyframes oakprobe { from { transform: translateX(0) } to { transform: translateX(300px) } }';
  document.head.appendChild(s);
  const d = document.createElement('div');
  d.id = 'oakprobe';
  d.style.cssText = 'position:fixed;top:0;left:0;width:40px;height:40px;background:#e11;z-index:99999;animation:oakprobe 1s linear infinite alternate';
  document.body.appendChild(d);
  return true;
})()
'@)
    # 3b. Video EN BUCLE en la salida (no hay capa de video de fondo todavia): se graba 1 s de un canvas animado con MediaRecorder y se reproduce
    #     con loop desde un blob; asi se mide el decodificador y la presentacion de video del renderizador de la salida, no solo CSS.
    $result.videoReady = Eval $ows @'
(async () => {
  const c = document.createElement('canvas'); c.width = 320; c.height = 180;
  const g = c.getContext('2d');
  const stream = c.captureStream(30);
  const rec = new MediaRecorder(stream, { mimeType: 'video/webm' });
  const chunks = [];
  rec.ondataavailable = (e) => chunks.push(e.data);
  const done = new Promise((r) => { rec.onstop = r; });
  let n = 0;
  const draw = setInterval(() => { g.fillStyle = `hsl(${(n++ * 12) % 360} 80% 50%)`; g.fillRect(0, 0, 320, 180); }, 33);
  rec.start(); await new Promise((r) => setTimeout(r, 1000)); rec.stop(); await done; clearInterval(draw);
  const v = document.createElement('video');
  v.id = 'oakprobevideo'; v.muted = true; v.loop = true; v.autoplay = true;
  v.style.cssText = 'position:fixed;top:50px;left:0;width:160px;height:90px;z-index:99999';
  v.src = URL.createObjectURL(new Blob(chunks, { type: 'video/webm' }));
  document.body.appendChild(v);
  await v.play();
  await new Promise((r) => setTimeout(r, 1500));   // al menos un ciclo completo del bucle
  return !v.paused;
})()
'@ 15000
    $measure = @'
new Promise((resolve) => {
  const t0 = performance.now();
  const a0 = document.getAnimations()[0]?.currentTime ?? -1;
  const v = document.getElementById('oakprobevideo');
  let frames = 0, videoFrames = 0;
  if (v && v.requestVideoFrameCallback) (function vf() { videoFrames++; v.requestVideoFrameCallback(vf); })();
  (function tick() {
    frames++;
    if (performance.now() - t0 >= 1000) resolve(JSON.stringify({ frames, cssAnimationAdvanced: (document.getAnimations()[0]?.currentTime ?? -1) !== a0, videoFrames, videoPlaying: !!v && !v.paused }));
    else requestAnimationFrame(tick);
  })();
})
'@
    $result.outputBefore = Eval $ows $measure
    $result.renderersBaseline = Get-Renderers

    if ($Full) {
        # 3c. Material para «Silencio» (solo -Full): un video WebM generado en la salida y un WAV sintetico, importados por la biblioteca real y puestos a sonar ANTES de congelar el panel.
        Add-Type -TypeDefinition (Get-Content (Join-Path $PSScriptRoot "AudioMeter.cs") -Raw)
        $src = Join-Path $data "origen"; New-Item -ItemType Directory $src | Out-Null
        $webm = Eval $ows @'
(async () => {
  const c = document.createElement("canvas"); c.width = 640; c.height = 360; document.body.appendChild(c);
  const g = c.getContext("2d"); let n = 0; const draw = setInterval(() => { g.fillStyle = "hsl(" + ((n++ * 9) % 360) + " 70% 40%)"; g.fillRect(0, 0, 640, 360); }, 33);
  const rec = new MediaRecorder(c.captureStream(30), { mimeType: "video/webm" }); const chunks = []; rec.ondataavailable = (e) => e.data.size && chunks.push(e.data);
  const done = new Promise((r) => { rec.onstop = r; }); rec.start(500); await new Promise((r) => setTimeout(r, 3000)); rec.stop(); await done; clearInterval(draw); c.remove();
  return await new Promise((resolve) => { const fr = new FileReader(); fr.onload = () => resolve(String(fr.result).split(",")[1]); fr.readAsDataURL(new Blob(chunks)); });
})()
'@ 20000
        [IO.File]::WriteAllBytes((Join-Path $src "silencio-video.webm"), [Convert]::FromBase64String($webm))
        $rate = 22050; $samples = $rate * 30; $bytes = New-Object byte[] (44 + $samples * 2)
        [Text.Encoding]::ASCII.GetBytes("RIFF").CopyTo($bytes, 0); [BitConverter]::GetBytes([int](36 + $samples * 2)).CopyTo($bytes, 4); [Text.Encoding]::ASCII.GetBytes("WAVEfmt ").CopyTo($bytes, 8)
        [BitConverter]::GetBytes([int]16).CopyTo($bytes, 16); [BitConverter]::GetBytes([int16]1).CopyTo($bytes, 20); [BitConverter]::GetBytes([int16]1).CopyTo($bytes, 22); [BitConverter]::GetBytes([int]$rate).CopyTo($bytes, 24)
        [BitConverter]::GetBytes([int]($rate * 2)).CopyTo($bytes, 28); [BitConverter]::GetBytes([int16]2).CopyTo($bytes, 32); [BitConverter]::GetBytes([int16]16).CopyTo($bytes, 34)
        [Text.Encoding]::ASCII.GetBytes("data").CopyTo($bytes, 36); [BitConverter]::GetBytes([int]($samples * 2)).CopyTo($bytes, 40)
        for ($i = 0; $i -lt $samples; $i++) { [BitConverter]::GetBytes([int16](12000 * [Math]::Sin(2 * [Math]::PI * 440 * $i / $rate))).CopyTo($bytes, 44 + 2 * $i) }
        [IO.File]::WriteAllBytes((Join-Path $src "silencio-pista.wav"), $bytes)
        $sendC = { param($type, $json) [void](Eval $cws ("window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'$type',payload:$json}));true")) }
        foreach ($name in "silencio-video.webm", "silencio-pista.wav") { & $sendC "media.import" (@{ path = (Join-Path $src $name); name = $null; folderId = $null } | ConvertTo-Json -Compress); Start-Sleep -Seconds 3 }
        $listJs = @'
(async () => { const id = crypto.randomUUID(); return await new Promise((resolve) => { window.external.receiveMessage((raw) => { const m = JSON.parse(raw); if (m.id === id) resolve(JSON.stringify(m.payload)); }); window.external.sendMessage(JSON.stringify({ v:@@V@@, id, kind: "query", type: "media.list", payload: { folderId: null, onlyUnfiled: false, kind: null, query: null, limit: null } })); }); })()
'@
        $items = ((Eval $cws $listJs 10000 | ConvertFrom-Json).result.items)
        $vid = $items | Where-Object { $_.kind -eq "video" } | Select-Object -First 1
        $wav = $items | Where-Object { $_.kind -eq "audio" } | Select-Object -First 1
        $result.silenceMaterialImported = [bool]($vid -and $wav)
        if ($vid -and $wav) {
            Start-Sleep -Seconds 2
            & $sendC "output.setMaster" (@{ master = "live" } | ConvertTo-Json -Compress)
            & $sendC "audio.setMasterVolume" (@{ volumePct = 20 } | ConvertTo-Json -Compress)
            & $sendC "media.play" (@{ mediaId = $vid.id; startPlaying = $true; volumePct = 100; muted = $false; loop = $true; fadeInMs = 0; fadeOutMs = 0; backdrop = "black"; onEnd = "back" } | ConvertTo-Json -Compress)
            & $sendC "media.play" (@{ mediaId = $wav.id; startPlaying = $true; volumePct = 100; muted = $false; loop = $true; fadeInMs = $null; fadeOutMs = $null; backdrop = "black"; onEnd = "back" } | ConvertTo-Json -Compress)
            Start-Sleep -Seconds 2
        }
    }

    # 4. Congelar el panel con un bucle infinito (no vuelve: se ignora su respuesta)
    $script:cdpId++
    $freeze = @{ id = $script:cdpId; method = "Runtime.evaluate"; params = @{ expression = "while (true) {}" } } | ConvertTo-Json -Depth 5 -Compress
    $cws.SendAsync([ArraySegment[byte]][Text.Encoding]::UTF8.GetBytes($freeze), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait(5000) | Out-Null
    Start-Sleep -Seconds 2

    # 5. El panel debe estar sin respuesta (nueva conexion al mismo destino: Runtime.evaluate no vuelve)
    $cws2 = Connect-Cdp $control.webSocketDebuggerUrl
    $panelAnswer = Eval $cws2 "1+1" 3000
    $result.panelAnswersAfterFreeze = ($null -ne $panelAnswer)

    # 6. La salida debe seguir viva
    $result.outputAfter = Eval $ows $measure
    $result.outputProcessResponsive = ($null -ne $result.outputAfter)

    # 7. Ventana principal: Windows no la considera colgada (el hilo de UI de C# sigue vivo)
    Add-Type -Namespace Probe -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);'
    $proc.Refresh()
    $result.isHungAppWindow = [Probe.Win]::IsHungAppWindow($proc.MainWindowHandle)

    if ($Full) {
        function Log-Text { (Get-ChildItem "$data\logs" -Filter *.log | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n" }
        Add-Type -Namespace Probe -Name Keys -MemberDefinition '[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);'
        function Press-Hotkey([byte]$vk) {
            foreach ($m in 0x11, 0x12, 0x10) { [Probe.Keys]::keybd_event($m, 0, 0, [UIntPtr]::Zero) }
            [Probe.Keys]::keybd_event($vk, 0, 0, [UIntPtr]::Zero); [Probe.Keys]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
            foreach ($m in 0x10, 0x12, 0x11) { [Probe.Keys]::keybd_event($m, 0, 2, [UIntPtr]::Zero) }
        }
        # La salida tiene capas (v7): se informa del texto visible del slide, de la cortina de Negro y de la de Logo.
        $readOutput = 'JSON.stringify({ text: [...document.querySelectorAll(".content-text")].filter((n) => getComputedStyle(n.closest("[data-layer=content]")).visibility === "visible").map((n) => n.textContent).join("|"), black: getComputedStyle(document.querySelector("[data-layer=black]")).display, logo: getComputedStyle(document.querySelector("[data-layer=logo]")).display })'

        # a) Latido perdido: con el panel congelado, el backend lo registra a los ~10 s
        Start-Sleep -Seconds 13
        $result.watchdogLogged = ((Log-Text) -match "El panel dej.+ de responder")
        # b) Por defecto la proyeccion NO muestra nada; con el ajuste activado (enviado desde la pagina de salida, porque el panel esta congelado) aparece el aviso
        $result.alertShownByDefault = [bool]((Eval $ows 'document.body.innerText.includes("PANEL SIN RESPUESTA")'))
        [void](Eval $ows "window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'settings.setPanelAlertOnOutput',payload:{enabled:true}}));true")
        Start-Sleep -Seconds 1
        $result.alertShownWhenEnabled = [bool]((Eval $ows 'document.body.innerText.includes("PANEL SIN RESPUESTA")'))
        $result.outputStillAnimating = (Eval $ows $measure)
        # c) Atajos globales de respaldo con el panel congelado: Negro (estado inicial = negro -> Live -> Negro), Logo, Limpiar texto
        # Para que haya algo que ocultar: Live y un slide (la salida los manda como cualquier ventana; el panel esta congelado).
        [void](Eval $ows "window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'output.showContent',payload:{blocks:['Texto de prueba'],theme:{fontFamily:'Arial',fontSize:72,color:'#FFFFFF',outlineWidth:0,outlineColor:'#000000',shadowBlur:0,shadowOffset:0,shadowColor:'#000000',align:'center',verticalAlign:'middle',lineHeight:1.2,marginTop:50,marginRight:50,marginBottom:50,marginLeft:50,autoFit:true,minFontSize:24},transitionMs:0}}));window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'output.setMaster',payload:{master:'live'}}));true")
        Start-Sleep -Seconds 1
        $seq = @()
        $seq += "inicial=" + (Eval $ows $readOutput)
        Press-Hotkey 0x42; Start-Sleep -Milliseconds 800; $seq += "tras B=" + (Eval $ows $readOutput)
        Press-Hotkey 0x42; Start-Sleep -Milliseconds 800; $seq += "tras B,B=" + (Eval $ows $readOutput)
        Press-Hotkey 0x4C; Start-Sleep -Milliseconds 800; $seq += "tras L=" + (Eval $ows $readOutput)
        Press-Hotkey 0x4C; Start-Sleep -Milliseconds 800; $seq += "tras L,L=" + (Eval $ows $readOutput)
        Press-Hotkey 0x43; Start-Sleep -Milliseconds 800; $seq += "tras C=" + (Eval $ows $readOutput)
        $result.hotkeys = ($seq -join " | ")

        # d) ALERTA con el panel congelado (v15/v16): se muestra desde la pagina de salida (el panel no responde) y se quita con el atajo GLOBAL Ctrl+Alt+Shift+A (pulsacion REAL).
        #    El atajo no pasa por el JavaScript del panel: tiene que funcionar igual con el panel atascado. La salida no debe verse afectada (la animacion sigue).
        $protoV = [regex]::Match((Get-Content (Join-Path $root "frontend\src\ipc\protocol.ts") -Raw), 'PROTOCOL_VERSION\s*=\s*(\d+)').Groups[1].Value
        $bandPresent = 'document.querySelector(".alert-band") !== null && getComputedStyle(document.querySelector(".alert-band")).opacity !== "0"'
        [void](Eval $ows ("window.external.sendMessage(JSON.stringify({v:$protoV,id:crypto.randomUUID(),kind:'command',type:'alert.show',payload:{text:'AVISO DE PRUEBA CON EL PANEL CONGELADO',mode:'band',durationSec:60}}));true"))
        $shown = $false; for ($i = 0; $i -lt 20 -and -not $shown; $i++) { Start-Sleep -Milliseconds 250; $shown = [bool](Eval $ows $bandPresent) }
        $result.alertShownWithFrozenPanel = $shown
        Press-Hotkey 0x41   # Ctrl+Alt+Shift+A
        $hidden = $false; for ($i = 0; $i -lt 20 -and -not $hidden; $i++) { Start-Sleep -Milliseconds 250; $hidden = -not [bool](Eval $ows 'document.querySelector(".alert-band") !== null')  }
        $result.alertHiddenByGlobalHotkeyWithFrozenPanel = $hidden
        $result.hideAlertHotkeyLogged = ((Log-Text) -match "Atajo global registrado: hideAlert")
        $result.alertWithFrozenPanelOutputStillAnimating = (Eval $ows $measure)

        # e) «SILENCIO» con el panel congelado (v18): la tecla GLOBAL Ctrl+Alt+Shift+S calla la pista y pausa el video; nada pasa por el JavaScript del panel. Se mide con el medidor de picos
        #    de Windows POR SESION de la aplicacion (no cuenta el sonido de otras aplicaciones). Un segundo pulso no reanuda nada.
        if ($result.silenceMaterialImported) {
            function Get-OakPids { $ids = New-Object System.Collections.Generic.List[int]; $ids.Add($proc.Id); Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape($data) } | ForEach-Object { $ids.Add([int]$_.ProcessId) }; return ,$ids.ToArray() }
            function Get-AppPeak([int]$ms) { $pids = Get-OakPids; $max = 0.0; $end = [DateTime]::UtcNow.AddMilliseconds($ms); while ([DateTime]::UtcNow -lt $end) { $p = [double][PeakMeter]::SessionPeak("", $pids); if ($p -gt $max) { $max = $p }; Start-Sleep -Milliseconds 25 }; return $max }
            $fgPaused = '(() => { const v = document.querySelector("[data-layer=foreground] video"); return !!v && v.paused; })()'
            # Los atajos de Negro/Logo/Limpiar de arriba dejaron la salida oculta y el video en pausa: se vuelve a poner todo en marcha DESDE LA PAGINA DE SALIDA (el panel esta congelado).
            [void](Eval $ows "window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'output.setMaster',payload:{master:'live'}}));window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'media.resume',payload:{target:'foreground'}}));window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'media.resume',payload:{target:'track'}}));true")
            Start-Sleep -Milliseconds 2500
            $result.silenceBeforePeak = [math]::Round((Get-AppPeak 800), 4)
            $result.silenceBeforeVideoPlaying = -not [bool](Eval $ows $fgPaused)
            Press-Hotkey 0x53   # Ctrl+Alt+Shift+S
            Start-Sleep -Milliseconds 900
            $result.silenceVideoPausedWithFrozenPanel = [bool](Eval $ows $fgPaused)
            $result.silenceAfterPeak = [math]::Round((Get-AppPeak 800), 4)
            $result.silenceOutputStillAnimating = (Eval $ows $measure)
            Press-Hotkey 0x53   # un segundo pulso NO reanuda nada
            Start-Sleep -Milliseconds 900
            $result.silenceSecondPressStillQuiet = ([bool](Eval $ows $fgPaused)) -and ((Get-AppPeak 600) -lt 0.001)
            $result.silenceHotkeyLogged = ((Log-Text) -match "Atajo global registrado: silence")
            $result.silenceOk = ($result.silenceBeforePeak -gt 0.005) -and $result.silenceBeforeVideoPlaying -and $result.silenceVideoPausedWithFrozenPanel -and ($result.silenceAfterPeak -lt 0.001) -and $result.silenceSecondPressStillQuiet
        }
    }

    if ($AfterFreeze) { $result.afterFreeze = (& $AfterFreeze $proc $Port $data | Out-String).Trim() }
}
finally {
    $result.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }   # solo el PID que arrancamos
    Start-Sleep -Seconds 1
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
