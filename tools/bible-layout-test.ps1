# PRUEBA DE LAYOUT REAL de la paginacion de la Biblia, sobre la APLICACION REAL (compilacion Debug) con la depuracion remota de WebView2 SOLO para esta prueba.
# happy-dom no mide texto: aqui el motor de layout es el de WebView2. La salida se abre en una VENTANA NORMAL en el monitor del panel (nunca en el proyector);
# los datos van a una carpeta temporal (OAK_DATA_DIR). Todo el texto es SINTETICO (palabras inventadas): ningun texto biblico.
#
# Que comprueba:
#  1. En el panel (window.__oakLayoutCheck, src/control/bible/layoutCheck.ts): 13 casos (versiculo corto, tipico, rango, muy largo de 1100 y 3000 caracteres,
#     palabras largas, 900 caracteres sin espacios, superindices 163-174, tema con contorno y sombra fuertes, aviso de derechos, fuente grande, margenes anchos,
#     serif) sobre el lienzo de 1920x1080 con la fuente y el tema reales: ninguna pagina pierde texto y todas caben segun la medicion. Tiempos de calculo.
#  2. EN LA VENTANA DE SALIDA REAL: cada pagina de cada caso se proyecta (output.showContent) y se mide lo que la salida pinta de verdad:
#     - el tamano de letra aplicado es el pedido (si el ajuste automatico tuviera que reducirlo, la medicion del panel no coincide con la de la salida),
#     - el texto no desborda su caja ni queda recortado (scrollHeight <= clientHeight), y deja libre el sangrado de la tinta (contorno + sombra),
#     - el alto medido por el panel coincide con el de la salida (tolerancia 1 px).
#  3. Que fuente pinta de verdad los digitos en superindice con cada fuente del editor de temas (CSS.getPlatformFontsForNode): informa si caen a otra fuente.
#
#   powershell -File tools\bible-layout-test.ps1 [-Shots] [-KeepData]
#   -Shots: guarda una captura PNG de la salida por caso en la carpeta temporal y muestra la ruta (para revisar a ojo: contorno, sombra, superindices).
# Sale con codigo 0 si todo pasa, 1 si algo falla.
param(
    [switch]$Shots,
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
if (-not $exe) { throw "Compila en Debug primero (npm run build en frontend/ y dotnet build)." }

$data = Join-Path $env:TEMP ("oak-bible-layout-" + [guid]::NewGuid().ToString("N"))
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
    $buffer = New-Object byte[] 1048576
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

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) { if (-not $ok) { $failures.Add($what); Write-Host "  FALLO: $what" -ForegroundColor Red } }

# Manda un comando al backend DESDE EL PANEL (lo mismo que hace la interfaz). El JSON se pasa a JS como cadena JSON (no como objeto en linea) para no depender de escapes.
function Send-FromPanel($cws, [string]$type, $payload) {
    $json = ($payload | ConvertTo-Json -Depth 10 -Compress)
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    [void](Eval $cws ("(() => { const p = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('" + $b64 + "'), c => c.charCodeAt(0)))); window.external.sendMessage(JSON.stringify({v:8,id:crypto.randomUUID(),kind:'command',type:'$type',payload:p})); return true; })()"))
}

# Lo que la SALIDA pinta de verdad ahora mismo (la ranura activa del crossfade).
$readOutput = @'
(() => {
  const slot = [...document.querySelectorAll('.content-slot')].find((s) => s.getAttribute('data-slot-active') === 'true');
  if (!slot) return null;
  const text = slot.querySelector('.content-text');
  if (!text || !text.firstElementChild) return null;
  const cs = getComputedStyle(text);
  return JSON.stringify({
    first: text.firstElementChild.textContent,
    blocks: text.children.length,
    fontSize: parseFloat(text.style.fontSize),
    textH: text.offsetHeight,
    boxH: slot.clientHeight,
    scrollH: slot.scrollHeight,
    family: cs.fontFamily,
  });
})()
'@

$result = [ordered]@{}
try {
    # 1. Panel y salida (ventana normal en el monitor del panel)
    $control = $null
    for ($i = 0; $i -lt 40 -and -not $control; $i++) { Start-Sleep -Milliseconds 500; $control = Get-Targets "control" | Where-Object { $_.type -eq "page" -and $_.url -match "index\.html" } | Select-Object -First 1 }
    if (-not $control) { throw "No aparecio el panel" }
    $cws = Connect-Cdp $control.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $list = Eval $cws '(async () => { const id = crypto.randomUUID(); return await new Promise((resolve) => { window.external.receiveMessage((raw) => { const m = JSON.parse(raw); if (m.id === id) resolve(JSON.stringify(m.payload)); }); window.external.sendMessage(JSON.stringify({ v: 8, id, kind: "query", type: "display.list", payload: {} })); }); })()'
    $panelId = ((($list | ConvertFrom-Json).result.displays) | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    if (-not $panelId) { throw "No se pudo leer el monitor del panel: $list" }
    Send-FromPanel $cws "output.open" @{ displayId = $panelId }
    $output = $null
    for ($i = 0; $i -lt 40 -and -not $output; $i++) { Start-Sleep -Milliseconds 500; $output = Get-Targets "output" | Where-Object { $_.type -eq "page" -and $_.url -match "output\.html" } | Select-Object -First 1 }
    if (-not $output) { throw "No aparecio la ventana de salida" }
    $ows = Connect-Cdp $output.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $result.chromium = Eval $ows "navigator.userAgent"
    Send-FromPanel $cws "output.setMaster" @{ master = "live" }
    Send-FromPanel $cws "output.setBackground" @{ kind = "color"; path = $null; color = "#1e3a8a"; muted = $true; transitionMs = 0 }

    # 2. Medicion y paginacion en el panel (motor de layout real)
    Write-Host "Paso 1: medicion y paginacion en el panel (WebView2)..." -ForegroundColor Cyan
    $raw = Eval $cws "typeof window.__oakLayoutCheck === 'function' ? JSON.stringify(window.__oakLayoutCheck()) : null" 120000
    if (-not $raw) { throw "El panel no expone window.__oakLayoutCheck (compila el frontend: npm run build)" }
    $report = $raw | ConvertFrom-Json
    $result.panelTotalMs = $report.totalMs
    Write-Host ("  {0,-24} {1,5} {2,8} {3,8} {4}" -f "caso", "pags", "ms", "limite", "veredicto")
    foreach ($c in $report.cases) {
        $ok = $c.lossless -and $c.allFit
        Write-Host ("  {0,-24} {1,5} {2,8} {3,8} {4}" -f $c.name, $c.pages.Count, $c.ms, $c.limit, $(if ($ok) { "ok" } else { "FALLO" }))
        Check $c.lossless "$($c.name): la paginacion perdio o altero texto"
        Check $c.allFit "$($c.name): una pagina no cabe segun la medicion del panel"
        Check ($c.pages.Count -ge 1) "$($c.name): sin paginas"
        Check ($c.ms -lt 150) "$($c.name): la paginacion tardo $($c.ms) ms en el hilo principal (presupuesto de la tarea ~50 ms; limite de la prueba 150)"
    }
    $byName = @{}; foreach ($c in $report.cases) { $byName[$c.name] = $c }
    Check ($byName["corto"].pages.Count -eq 1) "corto: deberia ser una sola pagina"
    Check ($byName["muy-largo-1100"].pages.Count -ge 2) "muy-largo-1100: deberia ocupar varias paginas (si no, la prueba no ejercita la paginacion)"
    Check ($byName["muy-largo-3000"].pages.Count -ge 4) "muy-largo-3000: deberia ocupar varias paginas"
    $result.maxPaginateMs = ($report.cases | Measure-Object -Property ms -Maximum).Maximum

    # 3. Cada pagina en la SALIDA real
    Write-Host "Paso 2: cada pagina proyectada en la ventana de salida real..." -ForegroundColor Cyan
    $shotDir = Join-Path $data "shots"; if ($Shots) { New-Item -ItemType Directory $shotDir | Out-Null }
    $pagesChecked = 0; $maxDelta = 0.0; $minHeadroom = 99999.0
    foreach ($c in $report.cases) {
        $pi = 0
        foreach ($p in $c.pages) {
            $pi++
            $blocks = @($p.blocks)
            Send-FromPanel $cws "output.showContent" @{ blocks = $blocks; theme = $c.projectionTheme; transitionMs = 0 }
            $expectedFirst = [string]$blocks[0]
            $m = $null
            for ($t = 0; $t -lt 40 -and -not $m; $t++) {
                Start-Sleep -Milliseconds 75
                $json = Eval $ows $readOutput
                if ($json) { $cand = $json | ConvertFrom-Json; if ($cand.first -eq $expectedFirst) { $m = $cand } }
            }
            $label = "$($c.name) p$pi/$($c.pages.Count)"
            if (-not $m) { Check $false "$label : la salida no llego a pintar la pagina"; continue }
            $pagesChecked++
            $delta = [math]::Abs($m.textH - $p.measuredHeight); if ($delta -gt $maxDelta) { $maxDelta = $delta }
            $headroom = $m.boxH - $m.textH - $c.inkBleed; if ($headroom -lt $minHeadroom) { $minHeadroom = $headroom }
            Check ($m.fontSize -eq $c.fontSize) "$label : la salida redujo la letra a $($m.fontSize) px (pedida $($c.fontSize)): la medicion del panel no coincide con la de la salida"
            Check ($m.textH -le $m.boxH) "$label : el texto desborda su caja ($($m.textH) > $($m.boxH))"
            Check ($m.scrollH -le $m.boxH) "$label : texto RECORTADO (scrollHeight $($m.scrollH) > $($m.boxH))"
            Check ($headroom -ge 0) "$label : no deja libre el sangrado de la tinta (contorno + sombra): faltan $(-$headroom) px"
            Check ($delta -le 1) "$label : el alto medido por el panel ($($p.measuredHeight)) difiere del de la salida ($($m.textH))"
            Check ($m.blocks -eq $blocks.Count) "$label : bloques distintos ($($m.blocks) vs $($blocks.Count))"
            if ($Shots -and $pi -eq 1) {
                $shot = Invoke-Cdp $ows "Page.captureScreenshot" @{ format = "png" } 15000
                if ($shot -and $shot.result.data) { [IO.File]::WriteAllBytes((Join-Path $shotDir ("{0}.png" -f $c.name)), [Convert]::FromBase64String($shot.result.data)) }
            }
        }
    }
    $result.pagesProjected = $pagesChecked
    $result.maxHeightDeltaPx = $maxDelta
    $result.minInkHeadroomPx = $minHeadroom
    if ($Shots) { Write-Host "  Capturas en $shotDir" }

    # 4. Que fuente pinta los superindices con cada fuente del editor de temas
    Write-Host "Paso 3: fuente que pinta los digitos en superindice (por fuente del editor de temas)..." -ForegroundColor Cyan
    [void](Invoke-Cdp $ows "DOM.enable"); [void](Invoke-Cdp $ows "CSS.enable")
    $fonts = @("Arial", "Segoe UI", "Calibri", "Georgia", "Times New Roman", "Verdana", "Tahoma", "Trebuchet MS")
    $superTheme = $report.cases[0].projectionTheme
    $sup = (0..9 | ForEach-Object { [char[]]@(0x2070, 0xB9, 0xB2, 0xB3, 0x2074, 0x2075, 0x2076, 0x2077, 0x2078, 0x2079)[$_] }) -join ""
    Write-Host ("  {0,-18} {1}" -f "fuente pedida", "fuente(s) que pintan los digitos en superindice (glifos)")
    $fallbacks = @()
    foreach ($f in $fonts) {
        $theme = $superTheme.PSObject.Copy(); $theme.fontFamily = $f; $theme.fontSize = 64
        Send-FromPanel $cws "output.showContent" @{ blocks = @($sup); theme = $theme; transitionMs = 0 }
        $ok = $false
        for ($t = 0; $t -lt 30 -and -not $ok; $t++) { Start-Sleep -Milliseconds 75; $json = Eval $ows $readOutput; if ($json -and (($json | ConvertFrom-Json).first -eq $sup)) { $ok = $true } }
        if (-not $ok) { Write-Host ("  {0,-18} (no se pinto)" -f $f); continue }
        $doc = Invoke-Cdp $ows "DOM.getDocument" @{ depth = 0 }
        $node = Invoke-Cdp $ows "DOM.querySelector" @{ nodeId = $doc.result.root.nodeId; selector = '.content-slot[data-slot-active="true"] .content-text' }
        $pf = Invoke-Cdp $ows "CSS.getPlatformFontsForNode" @{ nodeId = $node.result.nodeId }
        $used = @($pf.result.fonts | ForEach-Object { "{0} ({1})" -f $_.familyName, $_.glyphCount })
        Write-Host ("  {0,-18} {1}" -f $f, ($used -join ", "))
        $names = @($pf.result.fonts | ForEach-Object { $_.familyName })
        if ($names.Count -gt 1 -or ($names.Count -eq 1 -and $names[0] -ne $f)) { $fallbacks += $f }
    }
    $result.superscriptFallbackFonts = if ($fallbacks.Count -eq 0) { "ninguna" } else { $fallbacks -join ", " }

    Send-FromPanel $cws "output.clearContent" @{ transitionMs = 0 }
}
finally {
    $result.failures = $failures.Count
    $result.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }
    if ($failures.Count -gt 0) { Write-Host "`nFALLOS:" -ForegroundColor Red; $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red } }
    else { Write-Host "`nTODO CORRECTO" -ForegroundColor Green }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }   # solo el PID que arrancamos
    Start-Sleep -Seconds 1
    if ($KeepData -or $Shots) { Write-Host "Datos conservados en $data (logs en $data\logs)" } else { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($failures.Count -gt 0) { exit 1 }
