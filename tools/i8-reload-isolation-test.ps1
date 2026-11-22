# Recarga del panel (Ctrl+Alt+Shift+R) y aislamiento panel/salida, medidos con la aplicacion real (compilacion Debug) y pulsaciones reales de teclado.
# -Mode healthy: recarga con el panel sano y luego lo congela: la salida debe seguir.
# -Mode frozen : repite N veces (-Cycles) «congelar el panel -> recargar con el atajo -> comprobar que se recupera y que la salida no se vio afectada».
# Muestra, por ciclo: si el panel se recupera y en cuantos segundos, los renderizadores vivos con su CPU (un orfano atascado se veria al 100 %), y los fps de la salida
# mientras el panel esta congelado y despues de recuperarlo. Repetir tras actualizar Photino.NET, Photino.Native o WebView2 (ver CLAUDE.md).
param([ValidateSet("frozen", "healthy")][string]$Mode = "frozen", [int]$Cycles = 3)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
$data = Join-Path $env:TEMP ("oak-i8b-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $data | Out-Null
$env:OAK_DATA_DIR = $data; $env:OAK_NO_DIALOG = "1"; $env:WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS = "--remote-debugging-port=0"
$proc = Start-Process $exe.FullName -PassThru
Remove-Item Env:\WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS

# Con -Split cada navegador (panel / salida) elige su puerto: se lee de su carpeta de perfil (DevToolsActivePort)
function Get-PortFor($pattern) {
    $dir = if ($pattern -match "output") { "WebView2-output" } else { "WebView2" }
    $file = Join-Path $data "cache\$dir\EBWebView\DevToolsActivePort"
    if (Test-Path $file) { [int](Get-Content $file | Select-Object -First 1) } else { 0 }
}
function Get-Page($pattern) {
    $p = Get-PortFor $pattern; if ($p -eq 0) { return $null }
    try { Invoke-RestMethod "http://127.0.0.1:$p/json" -TimeoutSec 2 | Where-Object { $_.type -eq "page" -and $_.url -match $pattern } | Select-Object -First 1 } catch { $null }
}
function Connect-Cdp($url) { $ws = New-Object System.Net.WebSockets.ClientWebSocket; $ws.ConnectAsync([Uri][string]($url | Select-Object -First 1), [Threading.CancellationToken]::None).Wait(5000) | Out-Null; $ws }
$script:cdpId = 0
function Send-Cdp($ws, $method, $params) {
    $script:cdpId++
    $json = @{ id = $script:cdpId; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
    $ws.SendAsync([ArraySegment[byte]][Text.Encoding]::UTF8.GetBytes($json), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).Wait(5000) | Out-Null
    $script:cdpId
}
function Eval($ws, $expr, $timeoutMs = 8000) {
    try { $id = Send-Cdp $ws "Runtime.evaluate" @{ expression = $expr; awaitPromise = $true; returnByValue = $true } } catch { return $null }
    $deadline = [DateTime]::UtcNow.AddMilliseconds($timeoutMs); $buf = New-Object byte[] 65536
    while ([DateTime]::UtcNow -lt $deadline) {
        $sb = New-Object Text.StringBuilder
        do {
            $left = [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds; if ($left -le 0) { return $null }
            $cts = New-Object Threading.CancellationTokenSource($left)
            try { $r = $ws.ReceiveAsync([ArraySegment[byte]]$buf, $cts.Token).GetAwaiter().GetResult() } catch { return $null }
            [void]$sb.Append([Text.Encoding]::UTF8.GetString($buf, 0, $r.Count))
        } while (-not $r.EndOfMessage)
        $m = $sb.ToString() | ConvertFrom-Json
        if ($m.id -eq $id) { return $m.result.result.value }
    }
    $null
}
function Freeze($control) { $w = Connect-Cdp $control.webSocketDebuggerUrl; [void](Send-Cdp $w "Runtime.evaluate" @{ expression = "while (true) {}" }); $w }
Add-Type -Namespace Probe -Name Keys -MemberDefinition '[DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);'
function Press-Hotkey([byte]$vk) {
    foreach ($m in 0x11, 0x12, 0x10) { [Probe.Keys]::keybd_event($m, 0, 0, [UIntPtr]::Zero) }
    [Probe.Keys]::keybd_event($vk, 0, 0, [UIntPtr]::Zero); [Probe.Keys]::keybd_event($vk, 0, 2, [UIntPtr]::Zero)
    foreach ($m in 0x10, 0x12, 0x11) { [Probe.Keys]::keybd_event($m, 0, 2, [UIntPtr]::Zero) }
}
function Get-RendererIds {
    @(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape($data) -and $_.CommandLine -match "--type=renderer" } | Select-Object -ExpandProperty ProcessId | Sort-Object)
}
# % de un nucleo que consume cada renderizador durante $ms
function Sample-Cpu($ms = 1500) {
    $ids = Get-RendererIds; $before = @{}
    foreach ($id in $ids) { try { $before[$id] = (Get-Process -Id $id).TotalProcessorTime.TotalMilliseconds } catch { } }
    Start-Sleep -Milliseconds $ms
    ($ids | ForEach-Object { try { "{0}={1:N0}%" -f $_, (((Get-Process -Id $_).TotalProcessorTime.TotalMilliseconds - $before[$_]) * 100 / $ms) } catch { "$_=salio" } }) -join "  "
}
function Get-ProcessSummary {
    $procs = Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'" | Where-Object { $_.CommandLine -match [regex]::Escape($data) }
    $rows = $procs | ForEach-Object { $ty = if ($_.CommandLine -match "--type=(\S+)") { $Matches[1] } else { "browser" }; [pscustomobject]@{ Type = $ty; Mb = [math]::Round($_.WorkingSetSize / 1MB) } }
    (($rows | Group-Object Type | ForEach-Object { "{0}x{1} ({2} MB)" -f $_.Count, $_.Name, ($_.Group | Measure-Object Mb -Sum).Sum }) -join ", ") + " | TOTAL " + (($rows | Measure-Object Mb -Sum).Sum) + " MB"
}
$measure = 'new Promise((resolve) => { const t0 = performance.now(); let frames = 0; (function tick() { frames++; if (performance.now() - t0 >= 1000) resolve(frames); else requestAnimationFrame(tick); })(); })'
$out = [ordered]@{}
try {
    $control = $null
    for ($i = 0; $i -lt 40 -and -not $control; $i++) { Start-Sleep -Milliseconds 500; $control = Get-Page "index\.html" }
    $cws = Connect-Cdp $control.webSocketDebuggerUrl; Start-Sleep -Seconds 2
    $list = Eval $cws '(async () => { const id = crypto.randomUUID(); return await new Promise((resolve) => { window.external.receiveMessage((raw) => { const m = JSON.parse(raw); if (m.id === id) resolve(JSON.stringify(m.payload)); }); window.external.sendMessage(JSON.stringify({ v:10, id, kind: "query", type: "display.list", payload: {} })); }); })()'
    $panelId = ((($list | ConvertFrom-Json).result.displays) | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    [void](Eval $cws ("window.external.sendMessage(JSON.stringify({v:10,id:crypto.randomUUID(),kind:'command',type:'output.open',payload:{displayId:" + ($panelId | ConvertTo-Json -Compress) + "}}));true"))
    $output = $null
    for ($i = 0; $i -lt 40 -and -not $output; $i++) { Start-Sleep -Milliseconds 500; $output = Get-Page "output\.html" }
    $ows = Connect-Cdp $output.webSocketDebuggerUrl; Start-Sleep -Seconds 2

    $out.procesos = Get-ProcessSummary
    $out.procesos = Get-ProcessSummary
    $out.baseline = "renderizadores: " + ((Get-RendererIds) -join ",") + " | CPU: " + (Sample-Cpu 1000) + " | salida fps=" + (Eval $ows $measure)

    if ($Mode -eq "healthy") {
        Press-Hotkey 0x52; Start-Sleep -Seconds 3
        $out.afterHealthyReload = "renderizadores: " + ((Get-RendererIds) -join ",") + " | salida fps=" + (Eval $ows $measure)
        $c = Get-Page "index\.html"; $null = Freeze $c; Start-Sleep -Seconds 2
        $out.isolationAfter = "panel congelado tras la recarga: salida fps=" + $(if ($null -ne ($v = Eval $ows $measure 4000)) { $v } else { "SIN RESPUESTA (la salida se congeló con el panel)" })
    }
    else {
        for ($n = 1; $n -le $Cycles; $n++) {
            $c = Get-Page "index\.html"; $null = Freeze $c; Start-Sleep -Seconds 2
            $frozenFps = $(if ($null -ne ($v = Eval $ows $measure 4000)) { $v } else { "SIN RESPUESTA (la salida se congeló con el panel)" })
            $frozenCpu = Sample-Cpu 1000
            Start-Sleep -Seconds 9      # el atajo distingue «sin respuesta» (>10 s sin mensajes): hay que dejar pasar ese tiempo
            Press-Hotkey 0x52
            $recovered = $false; $secs = 0
            for ($i = 1; $i -le 25 -and -not $recovered; $i++) {
                Start-Sleep -Seconds 1; $secs = $i
                $c = Get-Page "index\.html"
                if ($c) { try { $w = Connect-Cdp $c.webSocketDebuggerUrl; $recovered = ((Eval $w "1+1" 2000) -eq 2) } catch { } }
            }
            $ui = if ($recovered) { Start-Sleep -Seconds 3; Eval $w 'document.body.innerText.includes("Controles maestros") && !document.body.innerText.includes("Conectando")' } else { "n/a" }
            $out["ciclo$n"] = "congelado: salida fps=$frozenFps CPU[$frozenCpu] | recarga: panel " + $(if ($recovered) { "RECUPERADO en ${secs}s" } else { "NO RECUPERADO tras ${secs}s" }) + ", interfaz viva=$ui | renderizadores: " + ((Get-RendererIds) -join ",") + " CPU[" + (Sample-Cpu 1000) + "] | salida fps=" + (Eval $ows $measure)
        }
        $log = (Get-ChildItem "$data\logs" -Filter *.log | ForEach-Object { Get-Content $_.FullName -Raw }) -join "`n"
        $out.log = "terminados: " + ([regex]::Matches($log, "Renderizador atascado del panel terminado").Count) + " | recargas: " + ([regex]::Matches($log, "Recarga del panel pedida").Count) + " | recuperaciones: " + ([regex]::Matches($log, "El panel volvi.+ a responder").Count)
    }
}
finally {
    $out.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }
    Start-Sleep -Seconds 1
    Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue
}