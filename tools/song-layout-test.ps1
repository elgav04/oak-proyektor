# PRUEBA DE LAYOUT REAL de las canciones, sobre la APLICACION REAL (compilacion Debug) con la depuracion remota de WebView2 SOLO para esta prueba.
# happy-dom no mide texto: aqui el motor de layout es el de WebView2. La salida se abre en una VENTANA NORMAL en el monitor del panel (nunca en el proyector);
# los datos van a una carpeta temporal (OAK_DATA_DIR). Todo el texto es SINTETICO (palabras inventadas): ninguna cancion real.
#
# Que comprueba (con el TEMA DE CANCIONES):
#  1. En el panel (window.__oakSongLayoutCheck, src/control/songs/layoutCheck.ts): 13 casos (estrofa corta, cortes manuales, estrofa larga con corte automatico, linea
#     larguisima, palabras largas, 700 caracteres sin espacios, contorno y sombra fuertes, fuente grande, margenes anchos, abajo a la izquierda, serif y una cancion
#     completa con repeticiones) sobre el lienzo de 1920x1080: ninguna diapositiva pierde ni mezcla texto, todas caben segun la medicion, los cortes manuales se
#     respetan, y el calculo por tandas cede el hilo (ninguna tanda larga). Informa tambien si el almacenamiento local de la pagina funciona (borrador sin guardar).
#  2. EN LA VENTANA DE SALIDA REAL: cada diapositiva se proyecta (output.showContent) y se mide lo que la salida pinta de verdad (letra pedida, sin desbordar ni
#     recortar, sangrado de la tinta libre, alto del panel = alto de la salida), tambien con la salida a otros factores de escala (-OutputScales) o en otro monitor
#     (-OutputOn other). Es la misma comprobacion que la de la Biblia: la pestana de canciones NO proyecta; esta prueba manda las diapositivas como lo hara la de proyeccion.
#  3. IPC v12 REAL (backend C# + base de datos temporal) desde la pagina: parseText, guardar, leer, actualizar, revision caducada (conflicto), historial, restaurar,
#     buscar y borrar; y el registro de la aplicacion NO contiene la letra.
#  4. La INTERFAZ REAL en WebView2: abrir la pestana, crear una cancion de 30 secciones escribiendo, ver las diapositivas con el tema sobre el lienzo, guardar con
#     Ctrl+S, el aviso 'sin guardar' (tambien en la pestana), el borrador en el almacenamiento local y que el panel no se atasca (tareas largas del hilo principal).
#
#   powershell -File tools\song-layout-test.ps1 [-Shots] [-KeepData] [-OutputOn panel|other] [-OutputScales 1.0,1.5,2.0]
# Sale con codigo 0 si todo pasa, 1 si algo falla.
param(
    [switch]$Shots,
    [switch]$KeepData,
    [ValidateSet("panel", "other")] [string]$OutputOn = "panel",
    [double[]]$OutputScales = @(1.0, 1.5, 2.0)
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$protoV = [regex]::Match((Get-Content (Join-Path $root "frontend\src\ipc\protocol.ts") -Raw), 'PROTOCOL_VERSION\s*=\s*(\d+)').Groups[1].Value
if (-not $protoV) { throw 'No se pudo leer PROTOCOL_VERSION de frontend\src\ipc\protocol.ts' }
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
if (-not $exe) { throw "Compila en Debug primero (npm run build en frontend/ y dotnet build)." }

$data = Join-Path $env:TEMP ("oak-song-layout-" + [guid]::NewGuid().ToString("N"))
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
    $expr = $expr.Replace('@@V@@', $protoV)   # la version del protocolo sale de frontend\src\ipc\protocol.ts: nunca un numero a mano
    $r = Invoke-Cdp $ws "Runtime.evaluate" @{ expression = $expr; awaitPromise = $true; returnByValue = $true } $timeoutMs
    if ($null -eq $r) { return $null }
    if ($r.result.exceptionDetails) { throw ("Error en la pagina: " + ($r.result.exceptionDetails | ConvertTo-Json -Depth 4 -Compress)) }
    return $r.result.result.value
}

$failures = New-Object System.Collections.Generic.List[string]
function Check([bool]$ok, [string]$what) { if (-not $ok) { $failures.Add($what); Write-Host "  FALLO: $what" -ForegroundColor Red } }

# Manda un comando al backend DESDE EL PANEL (lo mismo que hace la interfaz). El JSON se pasa a JS como cadena JSON codificada en base64.
function Send-FromPanel($cws, [string]$type, $payload) {
    $json = ($payload | ConvertTo-Json -Depth 10 -Compress)
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    [void](Eval $cws ("(() => { const p = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('" + $b64 + "'), c => c.charCodeAt(0)))); window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'$type',payload:p})); return true; })()"))
}

# Sonda IPC en la pagina del panel: registra TODOS los mensajes que llegan (se anade un receptor; el de la aplicacion sigue funcionando: se comprueba en el paso 4).
$probeJs = @'
(() => {
  if (window.__ipcProbe) return true;
  const log = [];
  window.__ipcProbe = {
    log,
    send(type, kind, payload) { const id = crypto.randomUUID(); window.external.sendMessage(JSON.stringify({ v: @@V@@, id, kind, type, payload })); return id; },
  };
  window.external.receiveMessage((raw) => { try { log.push(JSON.parse(raw)); } catch { /* ignorar */ } });
  return true;
})()
'@

function ConvertTo-B64Json($payload) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 10 -Compress))) }

# Una consulta real al backend: devuelve el reply { ok, result, error }.
function Invoke-Query($cws, [string]$type, $payload) {
    $b64 = ConvertTo-B64Json $payload
    $id = Eval $cws ("(() => { const p = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('" + $b64 + "'), c => c.charCodeAt(0)))); return window.__ipcProbe.send('$type', 'query', p); })()")
    for ($i = 0; $i -lt 80; $i++) {
        Start-Sleep -Milliseconds 100
        $found = Eval $cws "(() => { const m = window.__ipcProbe.log.find((x) => x.id === '$id'); return m ? JSON.stringify(m.payload) : null; })()"
        if ($found) { return ($found | ConvertFrom-Json) }
    }
    throw "Sin respuesta a la consulta $type"
}

# Un comando de escritura de canciones: espera su RESULTADO (song.changed o command.failed con el id del comando).
function Invoke-SongCommand($cws, [string]$type, $payload) {
    $b64 = ConvertTo-B64Json $payload
    $id = Eval $cws ("(() => { const p = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('" + $b64 + "'), c => c.charCodeAt(0)))); return window.__ipcProbe.send('$type', 'command', p); })()")
    for ($i = 0; $i -lt 100; $i++) {
        Start-Sleep -Milliseconds 100
        $found = Eval $cws "(() => { const m = window.__ipcProbe.log.find((x) => x.kind === 'event' && (x.type === 'song.changed' || x.type === 'command.failed') && x.payload.commandId === '$id'); return m ? JSON.stringify({ type: m.type, payload: m.payload }) : null; })()"
        if ($found) { return ($found | ConvertFrom-Json) }
    }
    throw "Sin resultado del comando $type"
}

# Lo que la SALIDA pinta de verdad ahora mismo (la ranura activa del crossfade).
$readOutput = @'
(() => {
  const slot = [...document.querySelectorAll('.content-slot')].find((s) => s.getAttribute('data-slot-active') === 'true');
  if (!slot) return null;
  const text = slot.querySelector('.content-text');
  if (!text || !text.firstElementChild) return null;
  return JSON.stringify({
    first: text.firstElementChild.textContent,
    blocks: text.children.length,
    fontSize: parseFloat(text.style.fontSize),
    textH: text.offsetHeight,
    boxH: slot.clientHeight,
    scrollH: slot.scrollHeight,
  });
})()
'@

function Test-OutputPages($report, [string]$label, [bool]$strict, [bool]$shots) {
    $s = [ordered]@{ pages = 0; maxDelta = 0.0; minHeadroom = 99999.0; shrunkPages = 0; minFontSeen = 99999.0 }
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
            $tag = "[$label] $($c.name) p$pi/$($c.pages.Count)"
            if (-not $m) { Check $false "$tag : la salida no llego a pintar la diapositiva"; continue }
            $s.pages++
            $delta = [math]::Abs($m.textH - $p.measuredHeight); if ($delta -gt $s.maxDelta) { $s.maxDelta = $delta }
            $headroom = $m.boxH - $m.textH - $c.inkBleed; if ($headroom -lt $s.minHeadroom) { $s.minHeadroom = $headroom }
            if ($m.fontSize -lt $c.fontSize) { $s.shrunkPages++ }
            if ($m.fontSize -lt $s.minFontSeen) { $s.minFontSeen = $m.fontSize }
            Check ($m.textH -le $m.boxH) "$tag : el texto desborda su caja ($($m.textH) > $($m.boxH))"
            Check ($m.scrollH -le $m.boxH) "$tag : texto RECORTADO (scrollHeight $($m.scrollH) > $($m.boxH))"
            Check ($headroom -ge 0) "$tag : no deja libre el sangrado de la tinta (contorno + sombra): faltan $(-$headroom) px"
            Check ($m.blocks -eq $blocks.Count) "$tag : bloques distintos ($($m.blocks) vs $($blocks.Count))"
            Check ($m.fontSize -le $c.fontSize -and $m.fontSize -ge $c.projectionTheme.minFontSize) "$tag : letra $($m.fontSize) px fuera de [minimo $($c.projectionTheme.minFontSize), pedida $($c.fontSize)]"
            if ($strict) {
                Check ($m.fontSize -eq $c.fontSize) "$tag : la salida redujo la letra a $($m.fontSize) px (pedida $($c.fontSize)): la medicion del panel no coincide con la de la salida"
                Check ($delta -le 1) "$tag : el alto medido por el panel ($($p.measuredHeight)) difiere del de la salida ($($m.textH))"
                Check ($headroom -ge $c.safetyMargin) "$tag : no queda la holgura de seguridad de ~0,25 renglones (quedan $headroom px, se reservaron $($c.safetyMargin))"
            }
            if ($shots -and $pi -eq 1) {
                $shot = Invoke-Cdp $ows "Page.captureScreenshot" @{ format = "png" } 15000
                if ($shot -and $shot.result.data) { [IO.File]::WriteAllBytes((Join-Path $shotDir ("{0}.png" -f $c.name)), [Convert]::FromBase64String($shot.result.data)) }
            }
        }
    }
    return [pscustomobject]$s
}

# Auxiliares de la pagina para manejar la interfaz real (sin dependencias: JS plano).
$uiJs = @'
window.__ui = {
  click(re) { const b = [...document.querySelectorAll('button')].find((x) => new RegExp(re).test(x.textContent) && !x.disabled); if (!b) return false; b.click(); return true; },
  setValue(selector, value) {
    const el = document.querySelector(selector); if (!el) return false;
    const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
    Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
    el.dispatchEvent(new Event('input', { bubbles: true })); return true;
  },
  key(selector, key, mods) { const el = selector ? document.querySelector(selector) : document.body; if (!el) return false; el.dispatchEvent(new KeyboardEvent('keydown', Object.assign({ key, bubbles: true, cancelable: true }, mods || {}))); return true; },
  badge() { const b = document.querySelector('[data-testid="dirty-badge"]'); return b ? b.textContent : null; },
  slides() { return document.querySelectorAll('[data-testid="slide-canvas"]').length; },
};
true
'@

$result = [ordered]@{}
try {
    # 1. Panel y salida (ventana normal en el monitor del panel)
    $control = $null
    for ($i = 0; $i -lt 40 -and -not $control; $i++) { Start-Sleep -Milliseconds 500; $control = Get-Targets "control" | Where-Object { $_.type -eq "page" -and $_.url -match "index\.html" } | Select-Object -First 1 }
    if (-not $control) { throw "No aparecio el panel" }
    $cws = Connect-Cdp $control.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    [void](Eval $cws $probeJs)
    $list = Invoke-Query $cws "display.list" @{}
    $displays = @($list.result.displays)
    $panelDisplay = $displays | Where-Object { $_.hostsPanel } | Select-Object -First 1
    $panelId = $panelDisplay.id
    if (-not $panelId) { throw "No se pudo leer el monitor del panel" }
    $openOn = $panelId
    if ($OutputOn -eq "other") {
        $other = $displays | Where-Object { -not $_.hostsPanel } | Select-Object -First 1
        if (-not $other) { throw "-OutputOn other necesita un segundo monitor (solo hay uno)." }
        $openOn = $other.id
        Write-Host ("Salida a pantalla completa en el OTRO monitor ({0}x{1}, escala {2}); el panel esta en un monitor con escala {3}." -f $other.width, $other.height, $other.scale, $panelDisplay.scale) -ForegroundColor Yellow
        $result.monitores = "panel: escala $($panelDisplay.scale) | salida: escala $($other.scale)"
    }
    Send-FromPanel $cws "output.open" @{ displayId = $openOn }
    $output = $null
    for ($i = 0; $i -lt 40 -and -not $output; $i++) { Start-Sleep -Milliseconds 500; $output = Get-Targets "output" | Where-Object { $_.type -eq "page" -and $_.url -match "output\.html" } | Select-Object -First 1 }
    if (-not $output) { throw "No aparecio la ventana de salida" }
    $ows = Connect-Cdp $output.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $result.chromium = Eval $ows "navigator.userAgent"
    $panelDpr = [double](Eval $cws "window.devicePixelRatio")
    $outputDpr = [double](Eval $ows "window.devicePixelRatio")
    $result.devicePixelRatio = "panel $panelDpr | salida $outputDpr"
    $sameScale = ($panelDpr -eq $outputDpr)
    if (-not $sameScale) { Write-Host "  El panel y la salida tienen DISTINTO devicePixelRatio ($panelDpr vs $outputDpr): pasada principal en modo \"sin recortes\"." -ForegroundColor Yellow }
    Send-FromPanel $cws "output.setMaster" @{ master = "live" }
    Send-FromPanel $cws "output.setBackground" @{ kind = "color"; path = $null; color = "#1e3a8a"; muted = $true; transitionMs = 0 }

    # 2. Medicion y derivacion en el panel (motor de layout real)
    Write-Host "Paso 1: medicion y derivacion de diapositivas en el panel (WebView2)..." -ForegroundColor Cyan
    $raw = Eval $cws "typeof window.__oakSongLayoutCheck === 'function' ? window.__oakSongLayoutCheck().then((r) => JSON.stringify(r)) : null" 120000
    if (-not $raw) { throw "El panel no expone window.__oakSongLayoutCheck (compila el frontend: npm run build)" }
    $report = $raw | ConvertFrom-Json
    $result.panelTotalMs = $report.totalMs
    Write-Host ("  {0,-44} {1,5} {2,8} {3,8} {4}" -f "caso", "diaps", "ms", "limite", "veredicto")
    foreach ($c in $report.cases) {
        $ok = $c.lossless -and $c.allFit
        Write-Host ("  {0,-44} {1,5} {2,8} {3,8} {4}" -f $c.name, $c.pages.Count, $c.ms, $c.limit, $(if ($ok) { "ok" } else { "FALLO" }))
        Check $c.lossless "$($c.name): la derivacion perdio, altero o mezclo texto de dos trozos"
        Check $c.allFit "$($c.name): una diapositiva no cabe segun la medicion del panel"
        Check ($c.pages.Count -ge 1) "$($c.name): sin diapositivas"
    }
    $byName = @{}; foreach ($c in $report.cases) { $byName[$c.name] = $c }
    Check ($byName["estrofa-corta-4-lineas"].pages.Count -eq 1) "estrofa corta: deberia ser una sola diapositiva"
    $two = $byName["dos-trozos-corte-manual"]
    Check ($two.pages.Count -ge 2 -and $two.boundaries[1] -eq "manual") "dos trozos: el corte manual (linea en blanco) debe separar las diapositivas (limites: $($two.boundaries -join ','))"
    $four = $byName["tres-cortes-manuales"]
    Check (@($four.boundaries | Where-Object { $_ -eq "manual" }).Count -ge 3) "tres cortes manuales: debe haber al menos 3 diapositivas que empiezan en un corte manual (limites: $($four.boundaries -join ','))"
    $long = $byName["estrofa-larga-14-lineas-corte-automatico"]
    Check ($long.pages.Count -ge 2 -and (@($long.boundaries) -contains "auto")) "estrofa larga: deberia partirse en un corte AUTOMATICO"
    Check ($byName["linea-larguisima"].pages.Count -ge 2) "linea larguisima: deberia ocupar varias diapositivas"
    $full = $byName["cancion-completa-con-repeticiones"]
    Check ($full.pages.Count -ge 10) "cancion completa: deberia tener al menos 10 diapositivas con el coro repetido"
    $result.maxSingleMeasureMs = $report.maxSingleMeasureMs
    Check ($report.maxSingleMeasureMs -lt 50) "una sola medicion tardo $($report.maxSingleMeasureMs) ms (presupuesto de una tarea del panel ~50 ms)"
    $result.slicedCalc = "$($report.sliced.slides) diapositivas en $($report.sliced.totalMs) ms, $($report.sliced.yields) cesiones del hilo, tanda mas larga $($report.sliced.maxSliceMs) ms"
    Check ($report.sliced.yields -ge 1) "el calculo de una cancion de 40 secciones no cedio nunca el hilo"
    Check ($report.sliced.maxSliceMs -lt 60) "una tanda del calculo trabajo $($report.sliced.maxSliceMs) ms seguidos (presupuesto de la tanda + una medicion; limite de la prueba 60)"
    $result.localStorage = if ($report.localStorageWorks) { "funciona (el borrador sin guardar se puede recuperar tras recargar)" } else { "NO funciona en esta pagina" }
    Check $report.localStorageWorks "el almacenamiento local de la pagina no funciona: el borrador sin guardar no sobreviviria a una recarga del panel"

    # 3. Cada diapositiva en la SALIDA real
    Write-Host "Paso 2: cada diapositiva proyectada en la ventana de salida real..." -ForegroundColor Cyan
    $shotDir = Join-Path $data "shots"; if ($Shots) { New-Item -ItemType Directory $shotDir | Out-Null }
    $main = Test-OutputPages $report "principal (salida a su escala real $outputDpr)" $sameScale $Shots
    $result.slidesProjected = $main.pages
    $result.maxHeightDeltaPx = $main.maxDelta
    $result.minInkHeadroomPx = $main.minHeadroom
    $result.mainShrunkSlides = $main.shrunkPages
    if ($Shots) { Write-Host "  Capturas en $shotDir" }

    if ($OutputScales.Count -gt 0) {
        Write-Host "Paso 2b: la salida a otros factores de escala ($($OutputScales -join ", ")): ningun recorte..." -ForegroundColor Cyan
        $dim = (Eval $ows 'JSON.stringify({ w: window.innerWidth, h: window.innerHeight })') | ConvertFrom-Json
        foreach ($sc in $OutputScales) {
            [void](Invoke-Cdp $ows "Emulation.setDeviceMetricsOverride" @{ width = [int]$dim.w; height = [int]$dim.h; deviceScaleFactor = [double]$sc; mobile = $false })
            Start-Sleep -Milliseconds 500
            $effective = [double](Eval $ows "window.devicePixelRatio")
            Check ([math]::Abs($effective - $sc) -lt 0.001) "la emulacion de escala $sc no se aplico (devicePixelRatio $effective)"
            $r = Test-OutputPages $report "escala x$sc" $false $false
            Write-Host ("  escala x{0}: {1} diapositivas, letra reducida en {2}, minimo visto {3} px, holgura minima de tinta {4} px, diferencia de alto maxima {5} px" -f $sc, $r.pages, $r.shrunkPages, $r.minFontSeen, $r.minHeadroom, $r.maxDelta)
            $result["escala x$sc"] = "diapositivas $($r.pages); letra reducida en $($r.shrunkPages); minimo $($r.minFontSeen) px; holgura tinta min $($r.minHeadroom) px; delta alto max $($r.maxDelta) px"
        }
        [void](Invoke-Cdp $ows "Emulation.clearDeviceMetricsOverride")
        Start-Sleep -Milliseconds 300
    }
    Send-FromPanel $cws "output.clearContent" @{ transitionMs = 0 }

    # 4. IPC v12 real (C# + SQLite temporal) desde la pagina
    Write-Host "Paso 3: canciones por el IPC v12 real (backend C# y base de datos temporal)..." -ForegroundColor Cyan
    $secret = "zafiralquimera"
    $text = "[Verso 1]`nLinea sintetica $secret uno`nOtra linea inventada`n`n[Coro]`nCoro inventado tenoko`n`n[Verso 2]`nSegunda estrofa vitola`n`nOrden: V1 C V2 C C"
    $parsed = Invoke-Query $cws "song.parseText" @{ text = $text; mergeIdenticalBlocks = $true }
    Check ($parsed.ok -and $parsed.result.sections.Count -eq 3) "song.parseText: deberia reconocer 3 secciones"
    Check ($parsed.ok -and (@($parsed.result.arrangement) -join ",") -eq "0,1,2,1,1") "song.parseText: el orden deberia ser 0,1,2,1,1 (fue $(@($parsed.result.arrangement) -join ','))"
    $sections = @($parsed.result.sections | ForEach-Object { @{ id = $null; kind = $_.kind; label = $_.label; text = $_.text } })
    $created = Invoke-SongCommand $cws "song.save" @{ id = $null; expectedRevision = $null; title = "Cancion de prueba"; author = "Autora inventada"; copyright = $null; ccliNumber = $null; notes = $null; background = $null; sections = $sections; arrangement = @($parsed.result.arrangement); reason = "save" }
    Check ($created.type -eq "song.changed" -and $created.payload.kind -eq "created") "song.save (crear): $($created | ConvertTo-Json -Compress)"
    $songId = $created.payload.songId
    $got = Invoke-Query $cws "song.get" @{ id = $songId }
    Check ($got.ok -and $got.result.revision -eq 1 -and $got.result.sections.Count -eq 3) "song.get: revision 1 con 3 secciones"
    Check ($got.ok -and $got.result.text -like "*Orden: Verso 1, Coro, Verso 2, Coro, Coro*") "song.get: el texto reconstruido debe llevar la linea Orden"
    $edited = @($got.result.sections | ForEach-Object { @{ id = $_.id; kind = $_.kind; label = $_.label; text = $(if ($_.label -eq "Coro") { "Coro inventado y editado" } else { $_.text }) } })
    $upd = Invoke-SongCommand $cws "song.save" @{ id = $songId; expectedRevision = 1; title = "Cancion de prueba"; author = "Autora inventada"; copyright = $null; ccliNumber = $null; notes = $null; background = $null; sections = $edited; arrangement = @($got.result.arrangement); reason = "save" }
    Check ($upd.type -eq "song.changed" -and $upd.payload.revision -eq 2) "song.save (actualizar con la revision vigente): $($upd | ConvertTo-Json -Compress)"
    $stale = Invoke-SongCommand $cws "song.save" @{ id = $songId; expectedRevision = 1; title = "Con la revision vieja"; author = $null; copyright = $null; ccliNumber = $null; notes = $null; background = $null; sections = $edited; arrangement = @($got.result.arrangement); reason = "save" }
    Check ($stale.type -eq "command.failed" -and $stale.payload.error.code -eq "song.conflict") "revision caducada: debia fallar con song.conflict (fue $($stale | ConvertTo-Json -Compress))"
    $hist = Invoke-Query $cws "song.history" @{ songId = $songId }
    Check ($hist.ok -and $hist.result.revisions.Count -eq 1) "song.history: deberia haber 1 copia previa"
    $restored = Invoke-SongCommand $cws "song.restoreRevision" @{ songId = $songId; revisionId = $null; expectedRevision = 2 }
    Check ($restored.type -eq "song.changed" -and $restored.payload.kind -eq "restored" -and $restored.payload.revision -eq 3) "song.restoreRevision: $($restored | ConvertTo-Json -Compress)"
    $back = Invoke-Query $cws "song.get" @{ id = $songId }
    Check ($back.ok -and (@($back.result.sections | Where-Object { $_.label -eq "Coro" }).text -eq "Coro inventado tenoko")) "tras restaurar, el coro vuelve a su texto original"
    $found = Invoke-Query $cws "song.search" @{ query = "zafiral"; limit = 10 }
    Check ($found.ok -and $found.result.songs.Count -eq 1) "song.search por prefijo de una palabra de la letra deberia encontrar la cancion"
    $foundTitle = Invoke-Query $cws "song.search" @{ query = "cancion"; limit = 10 }
    Check ($foundTitle.ok -and $foundTitle.result.songs.Count -eq 1) "song.search por titulo sin tildes"
    $del = Invoke-SongCommand $cws "song.delete" @{ id = $songId }
    Check ($del.type -eq "song.changed" -and $del.payload.kind -eq "deleted") "song.delete"
    $gone = Invoke-Query $cws "song.get" @{ id = $songId }
    Check ((-not $gone.ok) -and $gone.error.code -eq "song.notFound") "song.get de una cancion borrada: song.notFound"
    $result.ipcV12 = "parseText, save (crear y actualizar), conflicto por revision caducada, historial, restaurar, buscar y borrar: ok"
    # El tema de canciones: se guarda en settings.json por el backend, que lo valida (un valor invalido se rechaza y no cambia nada).
    $settings0 = Invoke-Query $cws "song.getSettings" @{}
    $theme = $settings0.result.theme
    $theme.fontSize = 52; $theme.minFontSize = 41
    Send-FromPanel $cws "song.setSettings" @{ theme = $theme; transitionMs = 250; atEnd = "clear" }
    $settings1 = Invoke-Query $cws "song.getSettings" @{}
    Check ($settings1.ok -and $settings1.result.theme.fontSize -eq 52 -and $settings1.result.transitionMs -eq 250 -and $settings1.result.atEnd -eq "clear") "song.setSettings: el tema valido deberia guardarse"
    Check ($settings1.result.theme.minFontSize -lt $settings1.result.theme.fontSize) "el minimo del ajuste automatico debe ser menor que el tamano"
    $file = Join-Path $data "settings.json"
    Check ((Test-Path $file) -and ((Get-Content $file -Raw) -match '"songDisplay"') -and ((Get-Content $file -Raw) -match '"atEnd": "clear"')) "settings.json deberia tener el bloque songDisplay con atEnd en texto"
    $badTheme = $settings1.result.theme.PSObject.Copy(); $badTheme.color = "rojo"
    Send-FromPanel $cws "song.setSettings" @{ theme = $badTheme; transitionMs = 250; atEnd = "clear" }
    Start-Sleep -Milliseconds 400
    $failedSettings = Eval $cws "(() => { const m = window.__ipcProbe.log.filter((x) => x.type === 'command.failed' && x.payload.commandType === 'song.setSettings').at(-1); return m ? m.payload.error.code : null; })()"
    Check ($failedSettings -eq "song.invalidSettings") "un color invalido debia rechazarse con song.invalidSettings (fue $failedSettings)"
    $settings2 = Invoke-Query $cws "song.getSettings" @{}
    Check ($settings2.result.theme.color -eq $settings1.result.theme.color) "tras el rechazo, el tema guardado no debe cambiar"
    Send-FromPanel $cws "song.setSettings" @{ theme = $settings0.result.theme; transitionMs = $settings0.result.transitionMs; atEnd = $settings0.result.atEnd }
    $result.temaGuardado = "song.setSettings: valido guardado en settings.json, invalido rechazado (song.invalidSettings), sin cambios"

    # 5. La interfaz REAL en WebView2
    Write-Host "Paso 4: la interfaz real (pestana Canciones) en WebView2..." -ForegroundColor Cyan
    [void](Eval $cws $uiJs)
    [void](Eval $cws "(() => { window.__longTasks = []; try { new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__longTasks.push(Math.round(e.duration)); }).observe({ entryTypes: ['longtask'] }); } catch (e) {} return true; })()")
    Check (Eval $cws "window.__ui.click('^Canciones')") "no se encontro la pestana Canciones"
    Start-Sleep -Milliseconds 800
    Check (Eval $cws "window.__ui.click('Canci.n nueva')") "no se encontro 'Canci.n nueva'"
    Start-Sleep -Milliseconds 500
    Check (Eval $cws "window.__ui.setValue('input[aria-label*=`"tulo de la canci`"]', 'Cancion larga de prueba')") "no se encontro el titulo"
    # Una cancion de 30 secciones con corte manual: se escribe de golpe en el cuadro de texto.
    $sb = New-Object Text.StringBuilder
    for ($i = 1; $i -le 30; $i++) {
        [void]$sb.Append("[Parte $i]`n")
        1..4 | ForEach-Object { [void]$sb.Append("Linea sintetica numero $i-$_ lumari tenoko sabiko`n") }
        [void]$sb.Append("`nSegundo trozo de la parte $i con palabras inventadas`n`n")
    }
    $longText = $sb.ToString()
    $b64Long = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($longText))
    $t0 = Get-Date
    Check (Eval $cws ("window.__ui.setValue('textarea[aria-label*=`"Texto de la canci`"]', new TextDecoder().decode(Uint8Array.from(atob('" + $b64Long + "'), c => c.charCodeAt(0))))")) "no se encontro el cuadro de texto"
    $slides = 0
    for ($i = 0; $i -lt 100 -and $slides -lt 60; $i++) { Start-Sleep -Milliseconds 100; $slides = [int](Eval $cws "window.__ui.slides()") }
    $elapsed = [int]((Get-Date) - $t0).TotalMilliseconds
    $result.uiSlidesRendered = "$slides miniaturas en $elapsed ms"
    Check ($slides -ge 60) "la interfaz deberia mostrar al menos 60 diapositivas para 30 secciones de 2 trozos (se vieron $slides)"
    $badge = Eval $cws "window.__ui.badge()"
    Check ($badge -match "NUEVA, SIN GUARDAR") "el aviso de estado deberia decir 'NUEVA, SIN GUARDAR' (decia: $badge)"
    $geometry = (Eval $cws "(() => { const c = document.querySelector('[data-testid=`"slide-canvas`"]'); const inner = c.firstElementChild; return JSON.stringify({ w: Math.round(c.getBoundingClientRect().width), innerW: parseFloat(getComputedStyle(inner).width), innerH: parseFloat(getComputedStyle(inner).height) }); })()") | ConvertFrom-Json
    Check ($geometry.innerW -eq 1920 -and $geometry.innerH -eq 1080) "la miniatura debe pintar sobre el lienzo de 1920x1080 (fue $($geometry.innerW)x$($geometry.innerH))"
    Check (Eval $cws "window.__ui.key('textarea', 's', { ctrlKey: true })") "no se pudo mandar Ctrl+S"
    $saved = $null
    for ($i = 0; $i -lt 60 -and -not $saved; $i++) { Start-Sleep -Milliseconds 150; $b = Eval $cws "window.__ui.badge()"; if ($b -match "Guardada") { $saved = $b } }
    Check ([bool]$saved) "Ctrl+S deberia dejar la cancion 'Guardada' (decia: $(Eval $cws 'window.__ui.badge()'))"
    $listAfter = Invoke-Query $cws "song.list" @{}
    $newSong = @($listAfter.result.songs) | Where-Object { $_.title -eq "Cancion larga de prueba" } | Select-Object -First 1
    Check ($null -ne $newSong) "la cancion guardada desde la interfaz debe estar en la lista"
    if ($newSong) {
        $full2 = Invoke-Query $cws "song.get" @{ id = $newSong.id }
        Check ($full2.result.sections.Count -eq 30) "la cancion guardada debe tener 30 secciones (tiene $($full2.result.sections.Count))"
    }
    # 'Sin guardar' tambien se ve en la pestana aunque estes en otra, y el borrador se escribe en el almacenamiento local.
    Check (Eval $cws "window.__ui.setValue('input[aria-label*=`"tulo de la canci`"]', 'Cancion larga de prueba (editada)')") "no se pudo editar el titulo"
    Start-Sleep -Milliseconds 900
    $draft = Eval $cws "localStorage.getItem('oak-proyektor.songs.draft')"
    Check ([bool]$draft -and $draft -match "editada") "el borrador sin guardar deberia estar en el almacenamiento local"
    Check (Eval $cws "window.__ui.click('^Biblia')") "no se encontro la pestana Biblia"
    Start-Sleep -Milliseconds 400
    Check ([bool](Eval $cws "!!document.querySelector('[data-testid=`"songs-dirty`"]')")) "la pestana Canciones deberia marcar los cambios sin guardar aunque se este en la Biblia"
    Check (Eval $cws "window.__ui.click('^Canciones')") "no se pudo volver a Canciones"
    Start-Sleep -Milliseconds 400
    Check ((Eval $cws "window.__ui.badge()") -match "SIN GUARDAR") "al volver, la cancion sigue 'SIN GUARDAR' (el controlador vive con la pagina)"
    Check (Eval $cws "window.__ui.key('textarea', 's', { ctrlKey: true })") "no se pudo guardar de nuevo"
    Start-Sleep -Milliseconds 1200
    Check ((Eval $cws "localStorage.getItem('oak-proyektor.songs.draft')") -eq $null) "al guardar, el borrador debe borrarse"
    $longTasks = @()
    foreach ($d in ((Eval $cws "JSON.stringify(window.__longTasks)") | ConvertFrom-Json)) { $longTasks += [int]$d }
    $maxLong = 0; foreach ($d in $longTasks) { if ($d -gt $maxLong) { $maxLong = $d } }
    $result.uiLongTasks = "$($longTasks.Count) tareas largas (>50 ms), la mayor de $maxLong ms"
    Check ($maxLong -lt 500) "el panel se atasco $maxLong ms seguidos con una cancion de 30 secciones (limite de la prueba 500 ms; presupuesto de una tarea ~50 ms)"
    # El panel sigue respondiendo y recibe mensajes del backend (la sonda no estropeo el receptor de la aplicacion).
    $still = Invoke-Query $cws "song.list" @{}
    Check $still.ok "el panel dejo de recibir respuestas"
    # 5b. PROYECCION real: lista -> tira -> Enter / flechas / maestros / Esc, con la salida real y el estado que confirma el backend
    Write-Host "Paso 5: proyectar la cancion desde la lista (salida real)..." -ForegroundColor Cyan
    if ($newSong) {
        $outText = { (Eval $ows "[...document.querySelectorAll('.content-text')].map((n) => n.textContent).join('|')") }
        $badgeNow = { Eval $cws "(() => { const b = document.querySelector('[data-testid=`"live-badge`"]'); return b ? b.textContent : null; })()" }
        $liveIdx = { Eval $cws "(() => { const l = [...document.querySelectorAll('[aria-label=`"Diapositivas derivadas`"] li')]; const i = l.findIndex((x) => x.dataset.live === 'true'); return i; })()" }
        $waitFor = { param($cond, [int]$ms = 4000) $end = (Get-Date).AddMilliseconds($ms); while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 100 }; return $false }
        Check (Eval $cws "window.__ui.key(null, 'Escape')") "no se pudo cerrar el editor"
        Start-Sleep -Milliseconds 500
        Check ([bool](Eval $cws "!!document.querySelector('[role=`"listbox`"]')")) "tras Esc deberia verse la lista de canciones"
        Check (Eval $cws "(() => { const o = document.querySelector('[data-song-id=`"$($newSong.id)`"]'); if (!o) return false; o.click(); return true; })()") "no se encontro la cancion en la lista"
        $t0 = Get-Date
        $strip = & $waitFor { [int](Eval $cws "document.querySelectorAll('[aria-label=`"Diapositivas derivadas`"] li').length") -ge 60 } 8000
        $result.proyTiraCargada = "tira de la cancion de 30 secciones en $([int]((Get-Date) - $t0).TotalMilliseconds) ms"
        $dbgJs = '(() => { const p = document.querySelector("[data-testid=projection]"); return "proj=" + window.__oakSongProjection() + " " + (p ? p.innerText.slice(0, 300) : "(sin panel) " + document.body.innerText.slice(0, 500).replace(/\n/g, " / ")); })()'
        Check $strip ("la tira de la cancion seleccionada deberia mostrar 60 diapositivas; panel: " + (Eval $cws $dbgJs))
        Check ((& $badgeNow) -eq "LISTO") "elegir la cancion no debe dejarla EN VIVO (decia: $(& $badgeNow))"
        Check ((& $outText) -notmatch "Linea sintetica") "elegir la cancion no debe proyectar nada"

        Check (Eval $cws "window.__ui.key(null, 'Enter')") "no se pudo pulsar Enter"
        Check (& $waitFor { (& $outText) -match "Linea sintetica numero 1-1" }) "Enter deberia proyectar la primera diapositiva en la salida real (salida: $(& $outText))"
        Check (& $waitFor { (& $badgeNow) -eq "EN VIVO" }) "con la salida mostrando el texto, el panel deberia decir EN VIVO por lo que confirma el backend (decia: $(& $badgeNow))"
        Check ((& $liveIdx) -eq 0) "la diapositiva en pantalla deberia ser la 0 (era $(& $liveIdx))"

        Check (Eval $cws "window.__ui.key(null, 'ArrowRight')") "no se pudo pulsar la flecha"
        Check (& $waitFor { (& $outText) -match "Segundo trozo de la parte 1\b" -and (& $outText) -notmatch "numero 1-1" }) "la flecha derecha deberia mostrar la segunda diapositiva (salida: $(& $outText))"

        # Rafaga: 40 pulsaciones seguidas -> la salida termina en la ultima y el panel marca esa diapositiva
        [void](Eval $cws "(() => { for (let i = 0; i < 40; i++) window.__ui.key(null, 'ArrowRight'); return true; })()")
        Check (& $waitFor { (& $outText) -match "Segundo trozo de la parte 21\b" -and (& $liveIdx) -eq 41 } 6000) "tras 40 flechas seguidas la salida deberia mostrar la diapositiva 41 (salida: $(& $outText); marcada: $(& $liveIdx))"

        # Controles maestros: ocultan lo proyectado y el panel lo dice; Live lo devuelve
        Send-FromPanel $cws "output.setMaster" @{ master = "black" }
        Check (& $waitFor { (& $badgeNow) -match "OCULTO .* Negro" }) "con Negro la insignia deberia decir OCULTO (decia: $(& $badgeNow))"
        Send-FromPanel $cws "output.setMaster" @{ master = "live" }
        Check (& $waitFor { (& $badgeNow) -eq "EN VIVO" }) "al volver a Live deberia decir EN VIVO (decia: $(& $badgeNow))"

        # Hasta el final: se queda en la ultima con FIN y la salida no se vacia
        [void](Eval $cws "(() => { for (let i = 0; i < 40; i++) window.__ui.key(null, 'ArrowRight'); return true; })()")
        Check (& $waitFor { (& $outText) -match "Segundo trozo de la parte 30\b" -and [bool](Eval $cws "!!document.querySelector('[data-testid=`"end-marker`"]')") } 6000) "al final deberia quedar la ultima diapositiva con el indicador FIN (salida: $(& $outText))"

        # Esc quita el texto
        Check (Eval $cws "window.__ui.key(null, 'Escape')") "no se pudo pulsar Esc"
        Check (& $waitFor { (& $outText) -notmatch "Linea sintetica|Segundo trozo" } 4000) "Esc deberia quitar el texto de la salida (salida: $(& $outText))"
        Check (& $waitFor { (& $badgeNow) -eq "LISTO" }) "tras Esc la insignia deberia volver a LISTO (decia: $(& $badgeNow))"
        $result.proyeccion = "Enter, flechas, rafaga de 40, Negro/Live, final con FIN y Esc: la salida real y la insignia coinciden con lo que confirma el backend"
    }
    if ($newSong) { [void](Invoke-SongCommand $cws "song.delete" @{ id = $newSong.id }) }

    # 6. La letra no esta en el registro de la aplicacion
    $logs = Get-ChildItem (Join-Path $data "logs") -Recurse -File -ErrorAction SilentlyContinue
    $leaks = @($logs | Where-Object { (Get-Content $_.FullName -Raw) -match "zafiralquimera|lumari|tenoko|Cancion larga|Cancion de prueba|Autora inventada" })
    Check ($leaks.Count -eq 0) "el registro de la aplicacion contiene letra, titulos o autores: $($leaks.Name -join ', ')"
    $result.logsSinLetra = "revisados $($logs.Count) archivo(s): ninguna palabra de la letra, titulos ni autores"
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
