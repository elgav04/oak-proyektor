# Prueba de los atajos del PANEL de las alertas (Ctrl+Mayus+K = ir al cuadro de alertas, Ctrl+Mayus+H = quitar la alerta) con PULSACIONES REALES de teclado (keybd_event) sobre la
# APLICACION REAL (compilacion Debug), con la depuracion remota de WebView2 SOLO para leer lo que llega a la pagina. La salida se abre en una VENTANA NORMAL (nunca en el proyector).
# Datos en una carpeta temporal. SOLO texto SINTETICO. Dice la distribucion de teclado activa (se queria comprobar con teclado espanol) y si WebView2 entrega la pulsacion a la pagina.
# Si la ventana del panel no consigue el foco (Windows lo impide a veces), la prueba lo dice y NO da nada por verificado: repitela con el panel en primer plano.
#   powershell -File tools\alert-keys-test.ps1 [-KeepData]
param(
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
if (-not $exe) { throw "Compila en Debug primero (npm run build en frontend/ y dotnet build)." }

$data = Join-Path $env:TEMP ("oak-altkeys-" + [guid]::NewGuid().ToString("N"))
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
    [void](Eval $ws ("window.external.sendMessage(JSON.stringify({v:16,id:crypto.randomUUID(),kind:'command',type:'$type',payload:$json}));true"))
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
$protoV = [regex]::Match((Get-Content (Join-Path $root "frontend\src\ipc\protocol.ts") -Raw), 'PROTOCOL_VERSION\s*=\s*(\d+)').Groups[1].Value
Add-Type -Namespace Probe -Name Win -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
[DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
[DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
[DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
public struct RECT { public int Left, Top, Right, Bottom; }
'@
function Press-Combo([byte[]]$mods, [byte]$vk) {
    foreach ($m in $mods) { [Probe.Win]::keybd_event($m, 0, 0, [UIntPtr]::Zero) }
    [Probe.Win]::keybd_event($vk, 0, 0, [UIntPtr]::Zero); [Probe.Win]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
    foreach ($m in ($mods | Sort-Object -Descending)) { [Probe.Win]::keybd_event($m, 0, 2, [UIntPtr]::Zero) }
}
$CTRL = 0x11; $SHIFT = 0x10
try {
    $control = $null
    for ($i = 0; $i -lt 40 -and -not $control; $i++) { Start-Sleep -Milliseconds 500; $control = Get-Targets "control" | Where-Object { $_.type -eq "page" -and $_.url -match "index\.html" } | Select-Object -First 1 }
    if (-not $control) { throw "No aparecio el panel" }
    $cws = Connect-Cdp $control.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $list = Eval $cws ('(async () => { const id = crypto.randomUUID(); return await new Promise((resolve) => { window.external.receiveMessage((raw) => { const m = JSON.parse(raw); if (m.id === id) resolve(JSON.stringify(m.payload)); }); window.external.sendMessage(JSON.stringify({ v:' + $protoV + ', id, kind: "query", type: "display.list", payload: {} })); }); })()')
    $panelId = ((($list | ConvertFrom-Json).result.displays) | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    if (-not $panelId) { throw "No se pudo leer el monitor del panel: $list" }
    $send = { param($type, $payload) $json = $payload | ConvertTo-Json -Depth 6 -Compress; [void](Eval $cws ("window.external.sendMessage(JSON.stringify({v:" + $protoV + ",id:crypto.randomUUID(),kind:'command',type:'$type',payload:$json}));true")) }
    # La salida NO se abre: su ventana normal taparia el panel y se llevaria el foco. Sin ventana, la alerta vive igual en el estado (el panel la marca «ALERTA OCULTA · Salida cerrada»).
    & $send "output.setMaster" @{ master = "live" }

    # Foco real en la ventana del panel
    [void](Invoke-Cdp $cws "Page.bringToFront")
    $proc.Refresh()
    # Windows impide tomar el primer plano a un proceso que no lo tiene: se engancha la entrada al hilo que lo tiene y se pulsa Alt (truco habitual) antes de pedirlo.
    $h = $proc.MainWindowHandle
    [void][Probe.Win]::ShowWindow($h, 9)
    $cur = [Probe.Win]::GetForegroundWindow(); $curPid = 0; $curThread = [Probe.Win]::GetWindowThreadProcessId($cur, [ref]$curPid); $me = [Probe.Win]::GetCurrentThreadId()
    [void][Probe.Win]::AttachThreadInput($me, $curThread, $true)
    [Probe.Win]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [Probe.Win]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero)
    [void][Probe.Win]::SetForegroundWindow($h)
    [void][Probe.Win]::AttachThreadInput($me, $curThread, $false)
    Start-Sleep -Milliseconds 500
    # El contenido web necesita el foco del TECLADO, no solo la ventana: un clic REAL en una zona vacia de la cabecera (a la derecha del titulo, sin botones).
    $rect = New-Object Probe.Win+RECT
    [void][Probe.Win]::GetWindowRect($h, [ref]$rect)
    [void][Probe.Win]::SetCursorPos($rect.Left + [int](($rect.Right - $rect.Left) * 0.55), $rect.Top + 78)
    [Probe.Win]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero); [Probe.Win]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 600
    $fg = [Probe.Win]::GetForegroundWindow(); $fgPid = 0; $thread = [Probe.Win]::GetWindowThreadProcessId($fg, [ref]$fgPid)
    $layout = [Probe.Win]::GetKeyboardLayout($thread).ToInt64() -band 0xFFFF
    $result.distribucionDeTeclado = ("0x{0:X4}" -f $layout) + $(if (($layout -band 0xFF) -eq 0x0A) { " (espanol)" } else { " (NO es espanol: no se comprueba con teclado espanol)" })
    $result.panelConElFoco = ($fgPid -eq $proc.Id)
    if ($fgPid -ne $proc.Id) { Write-Host "AVISO: la ventana del panel no tiene el foco (Windows lo impidio): las comprobaciones siguientes NO valen. Repite con el panel en primer plano." -ForegroundColor Yellow }

    # Grabadora de teclas: LO QUE LLEGA A LA PAGINA (en burbuja, despues de los manejadores del panel: `prevented` = alguien lo atendio)
    [void](Eval $cws 'window.__keys = []; window.addEventListener("keydown", (e) => { window.__keys.push({ key: e.key, code: e.code, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey, prevented: e.defaultPrevented }); }, false); true')
    $lastKey = { (Eval $cws 'JSON.stringify(window.__keys.at(-1) ?? null)') | ConvertFrom-Json }
    $status = { Eval $cws '(document.querySelector("[data-testid=alert-status]") || {}).innerText || ""' }

    Write-Host "Ctrl+Mayus+K (ir al cuadro de alertas)"
    # El foco empieza fuera del cuadro de alertas
    [void](Eval $cws 'document.activeElement && document.activeElement.blur(); true')
    Press-Combo @($CTRL, $SHIFT) 0x4B
    Start-Sleep -Milliseconds 400
    $k = & $lastKey
    Check ($null -ne $k -and $k.key -match '^[kK]$' -and $k.ctrl -and $k.shift -and -not $k.alt) "WebView2 entrega Ctrl+Mayus+K a la pagina (llego: $($k | ConvertTo-Json -Compress))"
    Check ($k.prevented -eq $true) "el panel la atendio (preventDefault)"
    Check ((Eval $cws 'document.activeElement && document.activeElement.id') -eq "alert-input") "el foco esta en el cuadro de alertas"

    Write-Host "Ctrl+Mayus+H (quitar la alerta), con el foco ESCRIBIENDO en el cuadro"
    & $send "alert.show" @{ text = "AVISO DE PRUEBA TECLADO"; mode = "band"; durationSec = 60 }
    for ($i = 0; $i -lt 20 -and -not (& $status); $i++) { Start-Sleep -Milliseconds 250 }
    Check ((& $status) -match "ALERTA") "hay una alerta en el estado antes de pulsar (indicacion del panel)"
    [void](Eval $cws 'document.getElementById("alert-input").focus(); true')
    Press-Combo @($CTRL, $SHIFT) 0x48
    Start-Sleep -Milliseconds 400
    $h = & $lastKey
    Check ($null -ne $h -and $h.key -match '^[hH]$' -and $h.ctrl -and $h.shift -and -not $h.alt) "WebView2 entrega Ctrl+Mayus+H a la pagina (llego: $($h | ConvertTo-Json -Compress))"
    Check ($h.prevented -eq $true) "el panel la atendio (preventDefault)"
    $gone = $false; for ($i = 0; $i -lt 20 -and -not $gone; $i++) { Start-Sleep -Milliseconds 250; $gone = -not (& $status) }
    Check $gone "la alerta se quito (el estado que confirma el backend ya no la trae)"

    Write-Host "Sin el modificador no hace nada"
    & $send "alert.show" @{ text = "AVISO DE PRUEBA TECLADO 2"; mode = "band"; durationSec = 60 }
    for ($i = 0; $i -lt 20 -and -not (& $status); $i++) { Start-Sleep -Milliseconds 250 }
    Press-Combo @($CTRL) 0x48
    Start-Sleep -Milliseconds 500
    Check ((& $status) -match "ALERTA") "Ctrl+H sin Mayus NO quita la alerta"
    & $send "alert.hide" @{}

    $result.FALLOS = if ($fails.Count -eq 0) { "ninguno" } else { $fails -join " || " }
}
finally {
    $result.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }   # solo el PID que arrancamos
    Start-Sleep -Seconds 1
    if ($KeepData) { Write-Host "Datos conservados en $data (logs en $data\logs)" } else { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($fails.Count -gt 0) { exit 1 }
