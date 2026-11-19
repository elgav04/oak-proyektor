# Prueba automatizada de I8 («el JS del panel se atasca»): arranca la aplicacion (compilacion Debug) con la depuracion remota de WebView2 SOLO para esta
# prueba, abre la salida en una VENTANA NORMAL (en el monitor del panel; no toca el proyector), congela el panel con `while(true){}` y mide si la salida
# sigue animandose. Repetir tras actualizar Photino.NET, Photino.Native o el runtime de WebView2 (ver CLAUDE.md).
# -Full: ademas comprueba (con el panel congelado) el latido perdido en el log, el aviso opcional en la salida, los atajos globales de respaldo
#        (con pulsaciones REALES de teclado: Ctrl+Alt+Shift+B/C/L) y la recarga del panel (Ctrl+Alt+Shift+R).
# Uso: powershell -File tools\i8-frozen-panel-test.ps1 [-AfterFreeze <ruta de un script a ejecutar con el panel congelado>]
param([string]$AfterFreeze = "", [switch]$Full)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
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
    send({ v: 5, id, kind: "query", type: "display.list", payload: {} });
  });
})()
'@
    $list = Eval $cws $open
    $result.displayList = $list
    $panelId = ((($list | ConvertFrom-Json).result.displays) | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    if (-not $panelId) { throw "No se pudo leer el monitor del panel: $list" }
    $cmd = "window.external.sendMessage(JSON.stringify({v:5,id:crypto.randomUUID(),kind:'command',type:'output.open',payload:{displayId:" + ($panelId | ConvertTo-Json -Compress) + "}}));true"
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
    $measure = @'
new Promise((resolve) => {
  const t0 = performance.now();
  const a0 = document.getAnimations()[0]?.currentTime ?? -1;
  let frames = 0;
  (function tick() {
    frames++;
    if (performance.now() - t0 >= 1000) resolve(JSON.stringify({ frames, cssAnimationAdvanced: (document.getAnimations()[0]?.currentTime ?? -1) !== a0 }));
    else requestAnimationFrame(tick);
  })();
})
'@
    $result.outputBefore = Eval $ows $measure
    $result.renderersBaseline = Get-Renderers

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
        $readOutput = 'JSON.stringify({ text: document.body.innerText.trim(), bg: [...document.querySelectorAll("div")].map((d) => getComputedStyle(d).backgroundColor).filter((c) => c !== "rgba(0, 0, 0, 0)")[0] })'

        # a) Latido perdido: con el panel congelado, el backend lo registra a los ~10 s
        Start-Sleep -Seconds 13
        $result.watchdogLogged = ((Log-Text) -match "El panel dej.+ de responder")
        # b) Por defecto la proyeccion NO muestra nada; con el ajuste activado (enviado desde la pagina de salida, porque el panel esta congelado) aparece el aviso
        $result.alertShownByDefault = [bool]((Eval $ows 'document.body.innerText.includes("PANEL SIN RESPUESTA")'))
        [void](Eval $ows "window.external.sendMessage(JSON.stringify({v:5,id:crypto.randomUUID(),kind:'command',type:'settings.setPanelAlertOnOutput',payload:{enabled:true}}));true")
        Start-Sleep -Seconds 1
        $result.alertShownWhenEnabled = [bool]((Eval $ows 'document.body.innerText.includes("PANEL SIN RESPUESTA")'))
        $result.outputStillAnimating = (Eval $ows $measure)
        # c) Atajos globales de respaldo con el panel congelado: Negro (estado inicial = negro -> Live -> Negro), Logo, Limpiar texto
        $seq = @()
        $seq += "inicial=" + (Eval $ows $readOutput)
        Press-Hotkey 0x42; Start-Sleep -Milliseconds 800; $seq += "tras B=" + (Eval $ows $readOutput)
        Press-Hotkey 0x42; Start-Sleep -Milliseconds 800; $seq += "tras B,B=" + (Eval $ows $readOutput)
        Press-Hotkey 0x4C; Start-Sleep -Milliseconds 800; $seq += "tras L=" + (Eval $ows $readOutput)
        Press-Hotkey 0x4C; Start-Sleep -Milliseconds 800; $seq += "tras L,L=" + (Eval $ows $readOutput)
        Press-Hotkey 0x43; Start-Sleep -Milliseconds 800; $seq += "tras C=" + (Eval $ows $readOutput)
        $result.hotkeys = ($seq -join " | ")
    }

    if ($AfterFreeze) { $result.afterFreeze = (& $AfterFreeze $proc $Port $data | Out-String).Trim() }
}
finally {
    $result.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }   # solo el PID que arrancamos
    Start-Sleep -Seconds 1
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}
