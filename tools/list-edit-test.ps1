# PRUEBA REAL del MODO EDITAR de la pestana «Lista», sobre la APLICACION REAL (compilacion Debug, backend C# + base de datos temporal) con la depuracion remota de
# WebView2 SOLO para esta prueba. La salida se abre en una VENTANA NORMAL en el monitor del panel (nunca en el proyector). Todo el texto es SINTETICO.
#
# Que comprueba (todo contra el backend REAL; cada paso verifica el estado con playlist.get / playlist.list, no solo la pantalla):
#  1. Crear una lista desde el formulario del panel (con fecha) y comprobar que existe en la base.
#  2. Anadir canciones desde la busqueda con el teclado real (/, escribir, Enter), una nota (con «limpiar al activar» apagado) y, desde la pestana Biblia, un pasaje con el
#     BOTON y con el ATAJO Ctrl+Mayus+A (se importa antes una Biblia SINTETICA).
#  3. Reordenar con el teclado real (Alt+flechas) y ARRASTRANDO con el raton (eventos de raton reales de CDP sobre @dnd-kit); quitar con Supr y recuperar con Ctrl+Z; duplicar.
#  4. Faltantes: se borra una cancion de la lista; el panel la marca con su motivo y se REEMPLAZA desde la busqueda. Duplicar la lista con un faltante: el aviso dice que se omitio.
#  5. Aviso al pasar a EN VIVO con un formulario sin terminar (Ctrl+E real): no cambia de modo; «Descartar y pasar» si.
#  6. Ya en EN VIVO, la lista armada se proyecta (Enter, flecha) en la salida real.
#  7. El panel no se atasca (tareas largas del hilo principal) y el registro de la aplicacion no contiene letra, titulos ni notas.
#
#   powershell -File tools\list-edit-test.ps1 [-Shots] [-KeepData]
# Sale con codigo 0 si todo pasa, 1 si algo falla.
param(
    [switch]$Shots,
    [switch]$KeepData
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
if (-not $exe) { throw "Compila en Debug primero (npm run build en frontend/ y dotnet build)." }

$data = Join-Path $env:TEMP ("oak-list-edit-" + [guid]::NewGuid().ToString("N"))
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
function ConvertTo-B64Json($payload) { [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($payload | ConvertTo-Json -Depth 10 -Compress))) }
$waitFor = { param($cond, [int]$ms = 4000) $end = (Get-Date).AddMilliseconds($ms); while ((Get-Date) -lt $end) { if (& $cond) { return $true }; Start-Sleep -Milliseconds 100 }; return $false }

# Sonda IPC en la pagina del panel (se anade un receptor; el de la aplicacion sigue funcionando).
$probeJs = @'
(() => {
  if (window.__ipcProbe) return true;
  const log = [];
  window.__ipcProbe = {
    log,
    send(type, kind, payload) { const id = crypto.randomUUID(); window.external.sendMessage(JSON.stringify({ v: 13, id, kind, type, payload })); return id; },
  };
  window.external.receiveMessage((raw) => { try { log.push(JSON.parse(raw)); } catch { /* ignorar */ } });
  window.__longTasks = [];
  try { new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__longTasks.push(Math.round(e.duration)); }).observe({ entryTypes: ['longtask'] }); } catch { /* sin soporte */ }
  return true;
})()
'@

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
# Un comando de escritura: espera su RESULTADO (el evento song.changed / playlist.changed o command.failed con el id del comando).
function Invoke-Write($cws, [string]$type, $payload) {
    $b64 = ConvertTo-B64Json $payload
    $id = Eval $cws ("(() => { const p = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('" + $b64 + "'), c => c.charCodeAt(0)))); return window.__ipcProbe.send('$type', 'command', p); })()")
    for ($i = 0; $i -lt 100; $i++) {
        Start-Sleep -Milliseconds 100
        $found = Eval $cws "(() => { const m = window.__ipcProbe.log.find((x) => x.kind === 'event' && (x.type === 'song.changed' || x.type === 'playlist.changed' || x.type === 'command.failed') && x.payload.commandId === '$id'); return m ? JSON.stringify({ type: m.type, payload: m.payload }) : null; })()"
        if ($found) { return ($found | ConvertFrom-Json) }
    }
    throw "Sin resultado del comando $type"
}
function Send-FromPanel($cws, [string]$type, $payload) {
    $b64 = ConvertTo-B64Json $payload
    [void](Eval $cws ("(() => { const p = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('" + $b64 + "'), c => c.charCodeAt(0)))); window.external.sendMessage(JSON.stringify({v:13,id:crypto.randomUUID(),kind:'command',type:'$type',payload:p})); return true; })()"))
}

# Teclas de CONFIANZA del navegador (no eventos sinteticos de la pagina): pasan por la misma tuberia que el teclado real dentro de la pagina.
$KEYS = @{
    Enter = @{ key = "Enter"; code = "Enter"; vk = 13 }; Right = @{ key = "ArrowRight"; code = "ArrowRight"; vk = 39 }; Left = @{ key = "ArrowLeft"; code = "ArrowLeft"; vk = 37 }
    Down = @{ key = "ArrowDown"; code = "ArrowDown"; vk = 40 }; Up = @{ key = "ArrowUp"; code = "ArrowUp"; vk = 38 }; Esc = @{ key = "Escape"; code = "Escape"; vk = 27 }
    E = @{ key = "e"; code = "KeyE"; vk = 69 }; Del = @{ key = "Delete"; code = "Delete"; vk = 46 }; Z = @{ key = "z"; code = "KeyZ"; vk = 90 }; D = @{ key = "d"; code = "KeyD"; vk = 68 }
    Slash = @{ key = "/"; code = "Slash"; vk = 191 }; A = @{ key = "A"; code = "KeyA"; vk = 65 }
}
function Press($ws, [string]$name, [switch]$Ctrl, [switch]$Alt, [switch]$Shift) {
    $k = $KEYS[$name]; $mods = 0; if ($Alt) { $mods += 1 }; if ($Ctrl) { $mods += 2 }; if ($Shift) { $mods += 8 }
    [void](Invoke-Cdp $ws "Input.dispatchKeyEvent" @{ type = "rawKeyDown"; key = $k.key; code = $k.code; windowsVirtualKeyCode = $k.vk; modifiers = $mods })
    [void](Invoke-Cdp $ws "Input.dispatchKeyEvent" @{ type = "keyUp"; key = $k.key; code = $k.code; windowsVirtualKeyCode = $k.vk; modifiers = $mods })
    Start-Sleep -Milliseconds 200
}
function Mouse($ws, [string]$type, [double]$x, [double]$y) {
    [void](Invoke-Cdp $ws "Input.dispatchMouseEvent" @{ type = $type; x = $x; y = $y; button = "left"; buttons = $(if ($type -eq "mouseReleased") { 0 } else { 1 }); clickCount = 1 })
}

# Ayudas de la pagina (se instalan una vez): clic por texto o etiqueta, enfocar, escribir un valor (con el evento de React) y leer filas/rectangulos.
$helpersJs = @'
(() => {
  window.__t = {
    btn(re, scope) { const root = scope ? document.querySelector(scope) : document; if (!root) return null; const r = new RegExp(re); return [...root.querySelectorAll('button')].find((b) => r.test(b.textContent) || r.test(b.getAttribute('aria-label') || '')) || null; },
    click(re, scope) { const b = this.btn(re, scope); if (!b) return false; b.click(); return true; },
    focus(sel) { const e = document.querySelector(sel); if (!e) return false; e.focus(); if (e.select) e.select(); return true; },
    setValue(sel, v) { const el = document.querySelector(sel); if (!el) return false; const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype; Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, v); el.dispatchEvent(new Event('input', { bubbles: true })); return true; },
    rows() { return JSON.stringify([...document.querySelectorAll('[data-edit-row]')].map((r) => ({ id: Number(r.dataset.editRow), title: r.getAttribute('aria-label').replace(/^\d+\.\s*/, ''), problem: r.dataset.problem === 'true', selected: r.dataset.selected === 'true' }))); },
    rect(sel) { const e = document.querySelector(sel); if (!e) return null; const r = e.getBoundingClientRect(); return JSON.stringify({ x: r.left + r.width / 2, y: r.top + r.height / 2, w: r.width, h: r.height }); },
    msg() { const m = document.querySelector('[data-testid="edit-message"]'); return m ? m.textContent : null; },
    mode() { const t = document.querySelector('[data-testid="list-tab"]'); return t ? t.dataset.mode : null; },
    tab(re) { const b = [...document.querySelectorAll('nav button')].find((x) => new RegExp(re).test(x.textContent)); if (!b) return false; b.click(); return true; },
  };
  return true;
})()
'@

$result = [ordered]@{}
try {
    # ───── 1. Panel y salida ─────
    $control = $null
    for ($i = 0; $i -lt 40 -and -not $control; $i++) { Start-Sleep -Milliseconds 500; $control = Get-Targets "control" | Where-Object { $_.type -eq "page" -and $_.url -match "index\.html" } | Select-Object -First 1 }
    if (-not $control) { throw "No aparecio el panel" }
    $cws = Connect-Cdp $control.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    [void](Eval $cws $probeJs)
    [void](Eval $cws $helpersJs)
    $displays = @((Invoke-Query $cws "display.list" @{}).result.displays)
    $panelId = ($displays | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    Send-FromPanel $cws "output.open" @{ displayId = $panelId }
    $output = $null
    for ($i = 0; $i -lt 40 -and -not $output; $i++) { Start-Sleep -Milliseconds 500; $output = Get-Targets "output" | Where-Object { $_.type -eq "page" -and $_.url -match "output\.html" } | Select-Object -First 1 }
    if (-not $output) { throw "No aparecio la ventana de salida" }
    $ows = Connect-Cdp $output.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    Send-FromPanel $cws "output.setMaster" @{ master = "live" }
    Send-FromPanel $cws "output.setBackground" @{ kind = "color"; path = $null; color = "#1e3a8a"; muted = $true; transitionMs = 0 }
    $outText = { (Eval $ows "[...document.querySelectorAll('.content-text')].map((n) => n.textContent).join('|')") }
    $uiRows = { $p = ConvertFrom-Json (Eval $cws "window.__t.rows()"); @($p | ForEach-Object { $_ }) }
    $uiTitles = { @(& $uiRows | ForEach-Object { $_.title }) }
    $mode = { Eval $cws "window.__t.mode()" }
    $getList = { param($id) (Invoke-Query $cws "playlist.get" @{ id = $id }).result }
    $backendTitles = { param($id) @((& $getList $id).items | ForEach-Object { $_.title }) }
    $allLists = { @((Invoke-Query $cws "playlist.list" @{}).result.playlists) }

    # ───── 2. Datos sinteticos por IPC real ─────
    Write-Host "Paso 1: canciones y una Biblia sinteticas (IPC real)..." -ForegroundColor Cyan
    function New-Song([string]$name, [int]$sections) {
        $secs = @(); for ($i = 1; $i -le $sections; $i++) { $secs += @{ id = $null; kind = "x"; label = "Verso $i"; text = "Estrofa sintetica $name $i`nsegunda linea sintetica $name $i" } }
        $r = Invoke-Write $cws "song.save" @{ id = $null; expectedRevision = $null; title = "Cancion sintetica $name"; author = $null; copyright = $null; ccliNumber = $null; notes = $null; background = $null; sections = $secs; arrangement = @(0..($sections - 1)); reason = $null }
        Check ($r.type -eq "song.changed") "no se pudo crear la cancion $name : $($r | ConvertTo-Json -Compress)"
        return $r.payload.songId
    }
    $songAlfa = New-Song "alfa" 3; $songBeta = New-Song "beta" 2; $songGamma = New-Song "gamma" 2; $songDelta = New-Song "delta" 2; $songEpsilon = New-Song "epsilon" 2
    $bibleFile = Join-Path $data "biblia-sintetica.json"
    $verses = @(); for ($v = 1; $v -le 5; $v++) { $verses += @{ type = "verse"; verse_numbers = @($v); lines = @("texto sintetico del versiculo $v") } }
    $bibleJson = @{ books = @(@{ usfm = "JHN"; chapters = @(@{ usfm = "JHN.3"; items = $verses }) }) } | ConvertTo-Json -Depth 10 -Compress
    [IO.File]::WriteAllText($bibleFile, $bibleJson, (New-Object Text.UTF8Encoding($false)))
    $b64 = ConvertTo-B64Json @{ path = $bibleFile; abbreviation = "TST"; name = "Biblia sintetica"; language = "es"; numberingProfile = "none"; attribution = ""; allowLargeDeletion = $false; allowLargeChange = $false }
    [void](Eval $cws ("(() => { const p = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('" + $b64 + "'), c => c.charCodeAt(0)))); window.__ipcProbe.send('bible.import', 'command', p); return true; })()"))
    $imported = & $waitFor { [bool](Eval $cws "window.__ipcProbe.log.some((x) => x.type === 'bible.importFinished' && x.payload.ok)") } 15000
    Check $imported "no se pudo importar la Biblia sintetica"

    # ───── 3. Modo EDITAR y crear la lista desde el formulario ─────
    Write-Host "Paso 2: crear la lista desde el formulario (teclado real)..." -ForegroundColor Cyan
    Check ([bool](Eval $cws "window.__t.tab('^Lista')")) "no se encontro la pestana Lista"
    Start-Sleep -Milliseconds 800
    Check ((& $mode) -eq "live") "la pestana deberia abrir en modo EN VIVO"
    Check ([bool](Eval $cws "/no hay listas/i.test(document.body.innerText)")) "sin listas deberia decirlo"
    Press $cws "E" -Ctrl
    Check (& $waitFor { (& $mode) -eq "edit" }) "Ctrl+E deberia pasar al modo EDITAR"
    Check ([bool](Eval $cws "window.__t.click('^Nueva lista')")) "no se encontro «Nueva lista»"
    Start-Sleep -Milliseconds 300
    Check ([bool](Eval $cws "window.__t.setValue('input[aria-label=`"Nombre de la lista`"]', 'Culto sintetico')")) "no se pudo escribir el nombre"
    Check ([bool](Eval $cws "window.__t.setValue('input[type=`"date`"]', '2026-11-29')")) "no se pudo escribir la fecha"
    Check ([bool](Eval $cws "window.__t.focus('input[aria-label=`"Nombre de la lista`"]')")) "no se pudo enfocar el nombre"
    Press $cws "Enter"
    $made = & $waitFor { @(& $allLists).Count -eq 1 } 6000
    Check $made "la lista deberia existir en la base de datos"
    $list = @(& $allLists)[0]
    $listId = $list.id
    Check ($list.name -eq "Culto sintetico" -and $list.serviceDate -eq "2026-11-29") "nombre y fecha en la base: $($list | ConvertTo-Json -Compress)"

    # ───── 4. Anadir canciones con el teclado ─────
    Write-Host "Paso 3: anadir canciones (busqueda con teclado), una nota y reordenar..." -ForegroundColor Cyan
    foreach ($name in "alfa", "beta", "gamma", "delta") {
        Press $cws "Slash"
        Check ((Eval $cws "document.activeElement && document.activeElement.getAttribute('aria-label')") -eq "Buscar una canción para añadir") "«/» deberia llevar el foco a la busqueda"
        Check ([bool](Eval $cws "window.__t.setValue('input[aria-label=`"Buscar una canción para añadir`"]', 'Cancion sintetica $name')")) "no se pudo escribir la busqueda"
        $found = & $waitFor { [bool](Eval $cws "!!document.querySelector('[data-testid=`"song-search`"] li[role=`"option`"]') && document.querySelector('[data-testid=`"song-search`"] li[role=`"option`"]').textContent.includes('$name')") } 4000
        Check $found "la busqueda de $name no dio resultados"
        Press $cws "Enter"
        $n = [array]::IndexOf(@("alfa", "beta", "gamma", "delta"), $name) + 1
        Check (& $waitFor { @(& $backendTitles $listId).Count -eq $n } 5000) "tras anadir $name la lista deberia tener $n elementos (tiene $(@(& $backendTitles $listId).Count))"
    }
    $expect4 = @("Cancion sintetica alfa", "Cancion sintetica beta", "Cancion sintetica gamma", "Cancion sintetica delta")
    Check ((@(& $backendTitles $listId) -join "|") -eq ($expect4 -join "|")) "orden en la base: $((@(& $backendTitles $listId)) -join ' | ')"
    Check (& $waitFor { (@(& $uiTitles) -join "|") -eq ($expect4 -join "|") }) "la lista del panel deberia coincidir: $((@(& $uiTitles)) -join ' | ')"

    # Una nota: «limpiar al activar» apagado por defecto.
    Check ([bool](Eval $cws "window.__t.click('^[+] Nota o separador')")) "no se encontro «+ Nota o separador»"
    Start-Sleep -Milliseconds 300
    Check (-not [bool](Eval $cws "document.querySelector('[data-testid=`"note-form`"] input[type=`"checkbox`"]').checked")) "«limpiar al activar» deberia estar APAGADO por defecto"
    [void](Eval $cws "window.__t.setValue('input[aria-label=`"Título de la nota`"]', 'Ofrendas sintetica')")
    [void](Eval $cws "window.__t.setValue('textarea[aria-label=`"Texto de la nota (opcional)`"]', 'Pasan los diaconos sinteticos')")
    Check ([bool](Eval $cws "window.__t.click('^Añadir después de la selección', '[data-testid=`"note-form`"]')")) "no se pudo anadir la nota"
    Check (& $waitFor { @(& $backendTitles $listId).Count -eq 5 } 5000) "la nota deberia estar en la base"
    $noteItem = (& $getList $listId).items | Where-Object { $_.itemType -eq "note" } | Select-Object -First 1
    Check ($noteItem.clearOnActivate -eq $false -and $noteItem.note -eq "Pasan los diaconos sinteticos") "la nota en la base: $($noteItem | ConvertTo-Json -Compress)"

    # Reordenar con el teclado real: la nota (seleccionada al anadirla) sube dos puestos con Alt+flecha arriba.
    $before = @(& $backendTitles $listId)
    # Tras anadir con Enter el foco sigue en el cuadro de busqueda (a proposito, para seguir anadiendo): las teclas de la lista no valen ahi; se hace clic fuera.
    [void](Eval $cws "document.activeElement && document.activeElement.blur(); true")
    Press $cws "Up" -Alt; Press $cws "Up" -Alt
    $moved = & $waitFor { (@(& $backendTitles $listId))[2] -eq "Ofrendas sintetica" } 6000
    Check $moved "Alt+↑ dos veces deberia subir la nota al 3.o puesto (base: $((@(& $backendTitles $listId)) -join ' | ')) | aviso del panel: $(Eval $cws 'window.__t.msg()') | filas: $(Eval $cws 'window.__t.rows()')"
    Check (& $waitFor { (@(& $uiTitles))[2] -eq "Ofrendas sintetica" }) "el panel deberia mostrar el mismo orden"

    # Reordenar ARRASTRANDO con el raton real (eventos de raton de CDP sobre @dnd-kit): la ultima fila (delta) pasa al principio.
    $rowsNow = @(& $uiRows); $lastId = $rowsNow[$rowsNow.Count - 1].id; $firstId = $rowsNow[0].id
    # Los dos extremos deben VERSE en la ventana (un raton real no llega a lo que esta fuera): se centra la mitad de la lista.
    [void](Eval $cws "(() => { const rows = [...document.querySelectorAll('[data-edit-row]')]; rows[Math.floor(rows.length / 2)].scrollIntoView({ block: 'center' }); return true; })()")
    Start-Sleep -Milliseconds 400
    [void](Eval $cws "window.__dragLog = []; for (const t of ['pointerdown', 'pointermove', 'pointerup']) window.addEventListener(t, (e) => window.__dragLog.push(t), true); true")
    $handle = ConvertFrom-Json (Eval $cws "window.__t.rect('[data-edit-row=`"$lastId`"] button[aria-label^=`"Arrastrar`"]')")
    $target = ConvertFrom-Json (Eval $cws "window.__t.rect('[data-edit-row=`"$firstId`"]')")
    Mouse $cws "mouseMoved" $handle.x $handle.y
    Mouse $cws "mousePressed" $handle.x $handle.y
    for ($s = 1; $s -le 14; $s++) { Mouse $cws "mouseMoved" $handle.x ($handle.y + ($target.y - 8 - $handle.y) * $s / 14); Start-Sleep -Milliseconds 40 }
    Mouse $cws "mouseReleased" $handle.x ($target.y - 8)
    $dragged = & $waitFor { (@(& $backendTitles $listId))[0] -eq "Cancion sintetica delta" } 6000
    $dragEvents = Eval $cws "JSON.stringify({ down: window.__dragLog.filter((x) => x === 'pointerdown').length, move: window.__dragLog.filter((x) => x === 'pointermove').length, up: window.__dragLog.filter((x) => x === 'pointerup').length })"
    Check $dragged "ARRASTRAR con el raton deberia llevar «delta» al principio (base: $((@(& $backendTitles $listId)) -join ' | ')) | eventos de puntero: $dragEvents | de $($handle | ConvertTo-Json -Compress) a $($target | ConvertTo-Json -Compress)"
    Check (& $waitFor { (@(& $uiTitles))[0] -eq "Cancion sintetica delta" }) "el panel deberia mostrar el arrastre"
    $afterDrag = & $getList $listId
    $result.revisionTrasEdiciones = "revision $($afterDrag.revision) tras crear + 4 canciones + nota + 2 movimientos + arrastre (cada edicion = 1 comando)"

    # Quitar con Supr, recuperar con Ctrl+Z, duplicar con Ctrl+D.
    $countBefore = @(& $backendTitles $listId).Count
    Press $cws "Del"
    Check (& $waitFor { @(& $backendTitles $listId).Count -eq ($countBefore - 1) } 5000) "Supr deberia quitar el elemento seleccionado"
    Press $cws "Z" -Ctrl
    Check (& $waitFor { @(& $backendTitles $listId).Count -eq $countBefore } 5000) "Ctrl+Z deberia recuperarlo"
    Press $cws "D" -Ctrl
    Check (& $waitFor { @(& $backendTitles $listId).Count -eq ($countBefore + 1) } 5000) "Ctrl+D deberia duplicar el elemento seleccionado"
    Press $cws "Del"
    Check (& $waitFor { @(& $backendTitles $listId).Count -eq $countBefore } 5000) "se quita el duplicado"

    # ───── 5. Pasaje desde la Biblia: boton y atajo ─────
    Write-Host "Paso 4: anadir un pasaje desde la pestana Biblia (boton y Ctrl+Mayus+A)..." -ForegroundColor Cyan
    Check ([bool](Eval $cws "window.__t.tab('^Biblia')")) "no se encontro la pestana Biblia"
    Start-Sleep -Milliseconds 800
    Check ([bool](Eval $cws "window.__t.focus('input[type=`"search`"]')")) "no se pudo enfocar la busqueda de la Biblia"
    Check ([bool](Eval $cws "window.__t.setValue('input[type=`"search`"]', 'jn 3:2-3')")) "no se pudo escribir la referencia"
    Check (& $waitFor { [bool](Eval $cws "document.querySelectorAll('[aria-label=`"Versículos`"] [role=`"option`"]').length >= 5") } 6000) "la Biblia sintetica deberia mostrar Juan 3"
    $itemsBefore = @(& $backendTitles $listId).Count
    Check ([bool](Eval $cws "window.__t.click('Añadir a la lista')")) "no se encontro el boton «Añadir a la lista»"
    Check (& $waitFor { @(& $backendTitles $listId).Count -eq ($itemsBefore + 1) } 6000) "el BOTON deberia anadir el pasaje"
    Check (& $waitFor { [bool](Eval $cws "(document.querySelector('[data-testid=`"add-status`"]') || {}).textContent && /se añadió/.test(document.querySelector('[data-testid=`"add-status`"]').textContent)") }) "el aviso de pasaje anadido deberia verse"
    Press $cws "A" -Ctrl -Shift
    Check (& $waitFor { @(& $backendTitles $listId).Count -eq ($itemsBefore + 2) } 6000) "el ATAJO Ctrl+Mayus+A deberia anadir el pasaje otra vez"
    $passages = @((& $getList $listId).items | Where-Object { $_.itemType -eq "passage" })
    Check ($passages.Count -eq 2) "deberia haber 2 pasajes"
    $p0 = $passages[0]
    Check ($p0.bookNumber -eq 43 -and $p0.chapter -eq 3 -and $p0.verseStart -eq 2 -and $p0.verseEnd -eq 3 -and $p0.bibleAbbreviation -eq "TST" -and $p0.resolution -eq "resolved") "el pasaje en la base: $($p0 | ConvertTo-Json -Compress)"
    Check ((($passages | ConvertTo-Json -Depth 6 -Compress) -notmatch "texto sintetico") -and $true) "el elemento NO debe llevar texto biblico"

    # ───── 6. Faltantes y duplicar ─────
    Write-Host "Paso 5: faltantes (reemplazar) y duplicar con un faltante..." -ForegroundColor Cyan
    Check ([bool](Eval $cws "window.__t.tab('^Lista')")) "no se pudo volver a Lista"
    Start-Sleep -Milliseconds 800
    Check ((& $mode) -eq "edit") "el modo EDITAR debe conservarse al cambiar de pestana"
    $del = Invoke-Write $cws "song.delete" @{ id = $songGamma }
    Check ($del.type -eq "song.changed" -and $del.payload.usedInLists.Count -eq 1 -and $del.payload.usedInLists[0].name -eq "Culto sintetico") "el borrado deberia nombrar la lista: $($del | ConvertTo-Json -Compress -Depth 5)"
    Check (& $waitFor { @(& $uiRows | Where-Object { $_.problem }).Count -eq 1 } 6000) "el panel deberia marcar 1 elemento con problema"
    $problemRow = @(& $uiRows | Where-Object { $_.problem })[0]
    Check ($problemRow.title -eq "Cancion sintetica gamma") "el faltante deberia conservar su titulo (era: $($problemRow.title))"
    Check ([bool](Eval $cws "/La canción ya no existe/.test(document.querySelector('[data-edit-row=`"$($problemRow.id)`"]').textContent) && !!window.__t.btn('Reemplazar por otra canción', '[data-edit-row=`"$($problemRow.id)`"]') && !!window.__t.btn('Quitar', '[data-edit-row=`"$($problemRow.id)`"]')")) "el faltante deberia decir su motivo y ofrecer reemplazar o quitar"

    # Duplicar CON el faltante: el aviso dice que se omitio.
    $listsBefore = @(& $allLists).Count
    Check ([bool](Eval $cws "window.__t.click('^Duplicar$')")) "no se encontro «Duplicar»"
    Start-Sleep -Milliseconds 300
    Check ([bool](Eval $cws "window.__t.click('^Duplicar$', '[data-testid=`"form-duplicate`"]')")) "no se pudo confirmar el duplicado"
    Check (& $waitFor { @(& $allLists).Count -eq ($listsBefore + 1) } 6000) "la copia deberia existir"
    Check (& $waitFor { [bool](Eval $cws "/OMITIERON 1 porque faltaba su canción/.test(window.__t.msg() || '')") } 6000) "el aviso de la copia deberia decir que se omitio 1 (decia: $(Eval $cws 'window.__t.msg()'))"
    $copy = @(& $allLists | Where-Object { $_.id -ne $listId })[0]
    Check ($copy.missingCount -eq 0 -and $copy.name -eq "Copia de Culto sintetico" -and $copy.serviceDate -eq "2026-12-06") "la copia en la base: $($copy | ConvertTo-Json -Compress)"
    Check ($copy.itemCount -eq ((& $getList $listId).items.Count - 1)) "la copia deberia tener un elemento menos (el faltante): $($copy.itemCount)"
    Check (((& $getList $listId).missingCount) -eq 1) "la lista ORIGINAL conserva su faltante"

    # Volver a la original y REEMPLAZAR el faltante por otra cancion.
    $sel = Eval $cws "(() => { const s = document.querySelector('select[aria-label=`"Lista del culto`"]'); const o = [...s.options].find((x) => x.textContent.startsWith('Culto sintetico')); return o ? o.value : null; })()"
    Check ([bool]$sel) "no se encontro la lista original en el selector"
    [void](Eval $cws "(() => { const s = document.querySelector('select[aria-label=`"Lista del culto`"]'); const setter = Object.getOwnPropertyDescriptor(HTMLSelectElement.prototype, 'value').set; setter.call(s, '$sel'); s.dispatchEvent(new Event('change', { bubbles: true })); return true; })()")
    Check (& $waitFor { @(& $uiRows | Where-Object { $_.problem }).Count -eq 1 } 6000) "la lista original deberia mostrar su faltante"
    $problemRow = @(& $uiRows | Where-Object { $_.problem })[0]
    $at = [array]::IndexOf(@(& $backendTitles $listId), "Cancion sintetica gamma")
    Check ([bool](Eval $cws "window.__t.click('Reemplazar por otra canción', '[data-edit-row=`"$($problemRow.id)`"]')")) "no se pudo pulsar «Reemplazar»"
    Start-Sleep -Milliseconds 300
    Check ([bool](Eval $cws "window.__t.setValue('input[aria-label=`"Buscar una canción para añadir`"]', 'epsilon')")) "no se pudo buscar"
    Check (& $waitFor { (Eval $cws "(() => { const o = [...document.querySelectorAll('[data-testid=`"song-search`"] li[role=`"option`"]')]; return o.length === 1 && o[0].textContent.includes('epsilon'); })()") -eq $true } 4000) "la busqueda del reemplazo deberia dar solo epsilon"
    Check ([bool](Eval $cws "window.__t.click('Usar esta', '[data-testid=`"song-search`"]')")) "no se pudo elegir la cancion de reemplazo"
    Check (& $waitFor { (& $getList $listId).missingCount -eq 0 } 6000) "tras reemplazar no deberia haber faltantes"
    Check ((@(& $backendTitles $listId))[$at] -eq "Cancion sintetica epsilon") "la nueva deberia ocupar el sitio del faltante (base: $((@(& $backendTitles $listId)) -join ' | '))"

    # ───── 7. Aviso al pasar a EN VIVO con una edicion pendiente ─────
    Write-Host "Paso 6: aviso al pasar a EN VIVO con una edicion pendiente..." -ForegroundColor Cyan
    Check ([bool](Eval $cws "window.__t.click('^Nueva lista')")) "no se encontro «Nueva lista»"
    Start-Sleep -Milliseconds 300
    [void](Eval $cws "window.__t.setValue('input[aria-label=`"Nombre de la lista`"]', 'A medias')")
    Eval $cws "document.activeElement && document.activeElement.blur(); true" | Out-Null
    Press $cws "E" -Ctrl
    Check (& $waitFor { [bool](Eval $cws "!!document.querySelector('[data-testid=`"leave-warning`"]')") }) "deberia verse el aviso de edicion pendiente"
    Check ((& $mode) -eq "edit") "NO deberia haber pasado a EN VIVO"
    Check ((Eval $cws "document.querySelector('input[aria-label=`"Nombre de la lista`"]').value") -eq "A medias") "lo escrito debe seguir en el formulario"
    $listsNow = @(& $allLists).Count
    Check ([bool](Eval $cws "window.__t.click('Descartar y pasar a EN VIVO')")) "no se encontro «Descartar y pasar a EN VIVO»"
    Check (& $waitFor { (& $mode) -eq "live" }) "deberia pasar a EN VIVO tras descartar"
    Check (@(& $allLists).Count -eq $listsNow) "descartar no debe crear ninguna lista"

    # ───── 8. Proyectar la lista armada ─────
    Write-Host "Paso 7: proyectar la lista armada (salida real)..." -ForegroundColor Cyan
    Check (& $waitFor { @(Eval $cws "document.querySelectorAll('[data-testid=`"items`"] [role=`"option`"]').length").Count -gt 0 -and [int](Eval $cws "document.querySelectorAll('[data-testid=`"items`"] [role=`"option`"]').length") -ge 7 } 6000) "la lista en vivo deberia mostrar los elementos"
    [void](Eval $cws "(() => { const o = document.querySelector('[data-testid=`"items`"] [role=`"option`"]'); if (o) o.click(); return true; })()")
    Press $cws "Enter"
    Check (& $waitFor { (& $outText) -match "Estrofa sintetica delta 1" }) "Enter deberia proyectar la primera cancion armada (delta, arrastrada al principio) en la salida real (salida: $(& $outText))"
    Press $cws "Right"
    Check (& $waitFor { (& $outText) -match "Estrofa sintetica delta 2" }) "la flecha derecha deberia mostrar la 2.a diapositiva (salida: $(& $outText))"
    Check (& $waitFor { (Eval $cws "(document.querySelector('[data-testid=`"live-badge`"]') || {}).textContent") -eq "EN VIVO" }) "la insignia deberia decir EN VIVO"

    # ───── 9. Panel sin bloqueos y sin letra en el registro ─────
    $longTasks = @(); foreach ($d in ((Eval $cws "JSON.stringify(window.__longTasks)") | ConvertFrom-Json)) { $longTasks += [int]$d }
    $maxLong = 0; foreach ($d in $longTasks) { if ($d -gt $maxLong) { $maxLong = $d } }
    $result.tareasLargas = "$($longTasks.Count) tareas largas (>50 ms) en el panel durante toda la edicion, la mayor de $maxLong ms"
    Check ($maxLong -lt 500) "el panel se atasco $maxLong ms seguidos (limite de la prueba 500 ms; presupuesto de una tarea ~50 ms)"
    if ($Shots) {
        $png = Invoke-Cdp $cws "Page.captureScreenshot" @{ format = "png" } 15000
        if ($png) { [IO.File]::WriteAllBytes((Join-Path $data "lista-live.png"), [Convert]::FromBase64String($png.result.data)); $result.captura = Join-Path $data "lista-live.png" }
    }
    $logs = Get-ChildItem (Join-Path $data "logs") -Recurse -File -ErrorAction SilentlyContinue
    $leaks = @($logs | Where-Object { (Get-Content $_.FullName -Raw) -match "Estrofa sintetica|segunda linea sintetica|Cancion sintetica|Culto sintetico|Ofrendas|diaconos|texto sintetico" })
    Check ($leaks.Count -eq 0) "el registro de la aplicacion contiene letra, titulos, nombres de lista o notas: $($leaks.Name -join ', ')"
    $result.logsSinLetra = "revisados $($logs.Count) archivo(s): ninguna palabra de la letra, titulos, nombres ni notas"
    $result.recorrido = "crear lista, anadir con teclado, nota, Alt+flechas, arrastre con raton, Supr/Ctrl+Z/Ctrl+D, pasaje (boton y Ctrl+Mayus+A), faltante+reemplazar, duplicar con faltante, aviso al salir, proyectar"
}
catch { $failures.Add("La prueba se interrumpio: $($_.Exception.Message)  [linea $($_.InvocationInfo.ScriptLineNumber)]") }
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
