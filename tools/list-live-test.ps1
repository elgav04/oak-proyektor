# PRUEBA REAL de la pestana «Lista» (modo EN VIVO), sobre la APLICACION REAL (compilacion Debug, backend C# + base de datos temporal) con la depuracion remota de
# WebView2 SOLO para esta prueba. happy-dom no mide ni habla con C#: aqui el motor de layout es el de WebView2 y las listas, el origin y el estado de la salida son los
# del backend real. La salida se abre en una VENTANA NORMAL en el monitor del panel (nunca en el proyector). Todo el texto es SINTETICO (palabras inventadas).
#
# Que comprueba:
#  1. Crea por IPC (comandos reales) dos canciones, una lista de HOY con: cancion A, una nota, una cancion que luego se BORRA (faltante real), cancion B, un pasaje con una
#     Biblia que no esta instalada y la MISMA cancion A otra vez. La interfaz muestra los 6 elementos, marca el faltante y el pasaje sin resolver y avisa arriba.
#  2. Teclas REALES del navegador (Input.dispatchKeyEvent, eventos de confianza) sobre la pestana: Enter proyecta; flecha derecha avanza; Ctrl+derecha salta de elemento
#     SALTANDO la nota, el faltante y el pasaje sin resolver; Ctrl+izquierda vuelve; la salida real pinta lo pedido.
#  3. La MISMA cancion dos veces: con el mismo texto en pantalla, la fila activa es la que se proyecto (por el origin que confirma el backend).
#  4. Insignia EN VIVO / OCULTO (Negro) / EN VIVO coherente con el estado real; «FIN · siguiente» al final de la ultima cancion; Esc quita el texto (LISTO).
#  5. RECARGA del panel (Page.reload): tras recargar, la fila activa y la insignia se recuperan solas del estado de la salida.
#  6. Cambiar de elemento NO escribe en la base de datos (la revision de la lista no cambia) y el panel no se atasca (tareas largas del hilo principal);
#     Ctrl+E alterna con el modo EDITAR (sin proyectar nada) y el modo EN VIVO no tiene controles de edicion.
#  7. El registro de la aplicacion no contiene la letra ni los titulos.
#
#   powershell -File tools\list-live-test.ps1 [-Shots] [-KeepData]
# Sale con codigo 0 si todo pasa, 1 si algo falla.
param(
    [switch]$Shots,
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
if (-not $exe) { throw "Compila en Debug primero (npm run build en frontend/ y dotnet build)." }

$data = Join-Path $env:TEMP ("oak-list-live-" + [guid]::NewGuid().ToString("N"))
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
    send(type, kind, payload) { const id = crypto.randomUUID(); window.external.sendMessage(JSON.stringify({ v: 15, id, kind, type, payload })); return id; },
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
    [void](Eval $cws ("(() => { const p = JSON.parse(new TextDecoder().decode(Uint8Array.from(atob('" + $b64 + "'), c => c.charCodeAt(0)))); window.external.sendMessage(JSON.stringify({v:15,id:crypto.randomUUID(),kind:'command',type:'$type',payload:p})); return true; })()"))
}

# Teclas de CONFIANZA del navegador (no eventos sinteticos de la pagina): pasan por la misma tuberia que el teclado real dentro de la pagina.
$KEYS = @{
    Enter = @{ key = "Enter"; code = "Enter"; vk = 13 }; Right = @{ key = "ArrowRight"; code = "ArrowRight"; vk = 39 }; Left = @{ key = "ArrowLeft"; code = "ArrowLeft"; vk = 37 }
    Down = @{ key = "ArrowDown"; code = "ArrowDown"; vk = 40 }; Up = @{ key = "ArrowUp"; code = "ArrowUp"; vk = 38 }; Esc = @{ key = "Escape"; code = "Escape"; vk = 27 }
    E = @{ key = "e"; code = "KeyE"; vk = 69 }
}
function Press($ws, [string]$name, [switch]$Ctrl) {
    $k = $KEYS[$name]; $mods = if ($Ctrl) { 2 } else { 0 }
    [void](Invoke-Cdp $ws "Input.dispatchKeyEvent" @{ type = "rawKeyDown"; key = $k.key; code = $k.code; windowsVirtualKeyCode = $k.vk; modifiers = $mods })
    [void](Invoke-Cdp $ws "Input.dispatchKeyEvent" @{ type = "keyUp"; key = $k.key; code = $k.code; windowsVirtualKeyCode = $k.vk; modifiers = $mods })
    Start-Sleep -Milliseconds 150
}

$readRows = @'
(() => JSON.stringify([...document.querySelectorAll('[data-testid="items"] [role="option"]')].map((r) => ({ id: Number(r.dataset.itemId), active: r.dataset.active === 'true', cursor: r.dataset.cursor === 'true', problem: r.dataset.problem === 'true', text: r.textContent }))))()
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
    $displays = @((Invoke-Query $cws "display.list" @{}).result.displays)
    $panelId = ($displays | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    if (-not $panelId) { throw "No se pudo leer el monitor del panel" }
    Send-FromPanel $cws "output.open" @{ displayId = $panelId }
    $output = $null
    for ($i = 0; $i -lt 40 -and -not $output; $i++) { Start-Sleep -Milliseconds 500; $output = Get-Targets "output" | Where-Object { $_.type -eq "page" -and $_.url -match "output\.html" } | Select-Object -First 1 }
    if (-not $output) { throw "No aparecio la ventana de salida" }
    $ows = Connect-Cdp $output.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    Send-FromPanel $cws "output.setMaster" @{ master = "live" }
    Send-FromPanel $cws "output.setBackground" @{ kind = "color"; path = $null; color = "#1e3a8a"; muted = $true; transitionMs = 0 }
    $outText = { (Eval $ows "[...document.querySelectorAll('.content-text')].map((n) => n.textContent).join('|')") }
    $badge = { Eval $cws "(() => { const b = document.querySelector('[data-testid=`"live-badge`"]'); return b ? b.textContent : null; })()" }
    $rows = { $parsed = ConvertFrom-Json (Eval $cws $readRows); @($parsed | ForEach-Object { $_ }) }   # PowerShell 5.1 entrega el arreglo JSON como UN objeto: se enumera
    $activeIds = { @(& $rows | Where-Object { $_.active } | ForEach-Object { $_.id }) }

    # 2. Datos sinteticos por IPC REAL
    Write-Host "Paso 1: crear canciones y una lista con comandos reales..." -ForegroundColor Cyan
    function New-Song([string]$name, [int]$sections) {
        $secs = @(); for ($i = 1; $i -le $sections; $i++) { $secs += @{ id = $null; kind = "x"; label = "Verso $i"; text = "Estrofa sintetica $name $i`nsegunda linea sintetica $name $i" } }
        $r = Invoke-Write $cws "song.save" @{ id = $null; expectedRevision = $null; title = "Cancion sintetica $name"; author = $null; copyright = $null; ccliNumber = $null; notes = $null; background = $null; sections = $secs; arrangement = @(0..($sections - 1)); reason = $null }
        Check ($r.type -eq "song.changed") "no se pudo crear la cancion $name : $($r | ConvertTo-Json -Compress)"
        return $r.payload.songId
    }
    $songA = New-Song "alfa" 4
    $songB = New-Song "beta" 3
    $songD = New-Song "delta" 2
    $today = Get-Date -Format "yyyy-MM-dd"
    $created = Invoke-Write $cws "playlist.create" @{ name = "Domingo sintetico"; serviceDate = $today; notes = $null }
    Check ($created.type -eq "playlist.changed") "playlist.create: $($created | ConvertTo-Json -Compress)"
    $listId = $created.payload.playlistId
    function Add-Item($item) { $r = Invoke-Write $cws "playlist.addItem" @{ playlistId = $listId; item = $item; position = $null }; Check ($r.type -eq "playlist.changed") "playlist.addItem: $($r | ConvertTo-Json -Compress)" }
    function New-ItemDto([hashtable]$over) { $d = @{ itemType = $null; songId = $null; bookNumber = $null; chapter = $null; verseStart = $null; verseEnd = $null; bibleAbbreviation = $null; title = $null; note = $null; clearOnActivate = $false }; foreach ($k in $over.Keys) { $d[$k] = $over[$k] }; return $d }
    Add-Item (New-ItemDto @{ itemType = "song"; songId = $songA })
    Add-Item (New-ItemDto @{ itemType = "note"; title = "Ofrendas"; note = "Pasan los diaconos" })
    Add-Item (New-ItemDto @{ itemType = "song"; songId = $songD })
    Add-Item (New-ItemDto @{ itemType = "song"; songId = $songB })
    Add-Item (New-ItemDto @{ itemType = "passage"; bookNumber = 43; chapter = 3; verseStart = 16; verseEnd = 17; bibleAbbreviation = "NOINSTALADA" })
    Add-Item (New-ItemDto @{ itemType = "song"; songId = $songA })
    $deleted = Invoke-Write $cws "song.delete" @{ id = $songD }   # el elemento de D queda FALTANTE de verdad
    Check ($deleted.type -eq "song.changed" -and $deleted.payload.usedInLists.Count -eq 1) "al borrar D el evento deberia nombrar la lista: $($deleted | ConvertTo-Json -Compress -Depth 5)"
    $got = (Invoke-Query $cws "playlist.get" @{ id = $listId }).result
    Check ($got.items.Count -eq 6 -and $got.missingCount -eq 1) "playlist.get: 6 elementos y 1 faltante (fue $($got.items.Count) y $($got.missingCount))"
    $revisionBefore = $got.revision
    $ids = @($got.items | ForEach-Object { $_.id })

    # 3. La interfaz
    Write-Host "Paso 2: la pestana Lista (interfaz real)..." -ForegroundColor Cyan
    Check ([bool](Eval $cws "(() => { const b = [...document.querySelectorAll('nav button')].find((x) => /^Lista/.test(x.textContent)); if (!b) return false; b.click(); return true; })()")) "no se encontro la pestana Lista"
    $ok = & $waitFor { @(& $rows).Count -eq 6 } 8000
    Check $ok "la lista deberia mostrar 6 elementos (mostro $(@(& $rows).Count))"
    $r0 = & $rows
    Check ($r0[2].problem -and $r0[2].text -match "Falta") "el elemento 3 (cancion borrada) deberia marcarse como faltante"
    Check ($r0[4].problem -and $r0[4].text -match "Biblia no instalada: NOINSTALADA") "el pasaje con una Biblia ausente deberia marcarse (decia: $($r0[4].text))"
    Check ([bool](Eval $cws "!!document.querySelector('[data-testid=`"missing-banner`"]')")) "deberia verse el aviso de elementos faltantes"
    Check ((& $badge) -eq "LISTO") "sin nada proyectado la insignia deberia decir LISTO (decia: $(& $badge))"
    Check ($r0[0].cursor) "el cursor deberia estar en el primer elemento proyectable"
    Check ((Eval $cws "document.querySelectorAll('[data-testid=`"items`"] button, [data-testid=`"items`"] input, [data-testid=`"items`"] [draggable=`"true`"]').length") -eq 0) "el modo EN VIVO no debe tener controles de edicion en la lista"

    # 4. Teclas reales
    Write-Host "Paso 3: teclas reales, saltos y salida real..." -ForegroundColor Cyan
    Press $cws "Enter"
    Check (& $waitFor { (& $outText) -match "Estrofa sintetica alfa 1" }) "Enter deberia proyectar la primera diapositiva de A en la salida real (salida: $(& $outText))"
    Check (& $waitFor { (& $badge) -eq "EN VIVO" }) "con el texto en la salida la insignia deberia decir EN VIVO (decia: $(& $badge))"
    Check (& $waitFor { (& $activeIds) -contains $ids[0] }) "la fila activa deberia ser la 1 (A): $(& $activeIds)"
    Press $cws "Right"
    Check (& $waitFor { (& $outText) -match "Estrofa sintetica alfa 2" }) "la flecha derecha deberia mostrar la 2.a diapositiva (salida: $(& $outText))"
    Press $cws "Right" -Ctrl
    Check (& $waitFor { (& $outText) -match "Estrofa sintetica beta 1" }) "Ctrl+derecha deberia saltar a B SALTANDO la nota, el faltante y el pasaje sin resolver (salida: $(& $outText))"
    Check (& $waitFor { ((& $activeIds) -join ",") -eq [string]$ids[3] }) "la fila activa deberia ser B (la 4) (era: $((& $activeIds) -join ','))"
    Press $cws "Left" -Ctrl
    Check (& $waitFor { (& $outText) -match "Estrofa sintetica alfa 1" }) "Ctrl+izquierda deberia volver a A (salida: $(& $outText))"
    Check (& $waitFor { ((& $activeIds) -join ",") -eq [string]$ids[0] }) "la fila activa deberia volver a ser la 1 (era: $((& $activeIds) -join ','))"

    # 5. La MISMA cancion dos veces
    Press $cws "Right" -Ctrl; Check (& $waitFor { (& $outText) -match "Estrofa sintetica beta 1" }) "salto a B"
    Press $cws "Right" -Ctrl
    Check (& $waitFor { (& $outText) -match "Estrofa sintetica alfa 1" -and ((& $activeIds) -join ",") -eq [string]$ids[5] }) "con A repetida, el MISMO texto de la 1.a vuelve a salir pero la fila activa debe ser la ULTIMA (la 6): activas=$((& $activeIds) -join ',')"

    # 6. FIN y maestros
    for ($i = 0; $i -lt 3; $i++) { Press $cws "Right" }
    Check (& $waitFor { (& $outText) -match "Estrofa sintetica alfa 4" }) "deberia llegar a la ultima diapositiva de A (salida: $(& $outText))"
    Press $cws "Right"
    Check (& $waitFor { [bool](Eval $cws "!!document.querySelector('[data-testid=`"end-marker`"]')") }) "una pulsacion de mas deberia dejar el marcador FIN"
    $endText = Eval $cws "(() => { const e = document.querySelector('[data-testid=`"end-marker`"]'); return e ? e.textContent : ''; })()"
    Check ($endText -match "FIN") "el marcador deberia decir FIN (decia: $endText)"
    Check ((& $outText) -match "Estrofa sintetica alfa 4") "al llegar al FIN la salida NO se vacia"
    Send-FromPanel $cws "output.setMaster" @{ master = "black" }
    Check (& $waitFor { (& $badge) -match "OCULTO .* Negro" }) "con Negro la insignia deberia decir OCULTO (decia: $(& $badge))"
    Send-FromPanel $cws "output.setMaster" @{ master = "live" }
    Check (& $waitFor { (& $badge) -eq "EN VIVO" }) "al volver a Live: EN VIVO (decia: $(& $badge))"

    # 7. Recargar el panel: el activo se recupera del estado de la salida
    Write-Host "Paso 4: recargar el panel..." -ForegroundColor Cyan
    [void](Invoke-Cdp $cws "Page.reload" @{} 15000)
    Start-Sleep -Seconds 3
    [void](Eval $cws $probeJs)
    Check ([bool](Eval $cws "(() => { const b = [...document.querySelectorAll('nav button')].find((x) => /^Lista/.test(x.textContent)); if (!b) return false; b.click(); return true; })()")) "tras recargar no se encontro la pestana Lista"
    Check (& $waitFor { @(& $rows).Count -eq 6 } 8000) "tras recargar la lista deberia volver a mostrarse"
    Check (& $waitFor { ((& $activeIds) -join ",") -eq [string]$ids[5] } 6000) "tras RECARGAR la fila activa deberia seguir siendo la 6 (derivada del origin de la salida): activas=$((& $activeIds) -join ',')"
    Check (& $waitFor { (& $badge) -eq "EN VIVO" } 4000) "tras recargar la insignia deberia decir EN VIVO (decia: $(& $badge))"
    Press $cws "Left"
    Check (& $waitFor { (& $outText) -match "Estrofa sintetica alfa 3" }) "tras recargar, la flecha izquierda continua desde donde estaba la salida (salida: $(& $outText))"

    # 8. Esc, modo EDITAR y que no se escribio nada
    Press $cws "Esc"
    Check (& $waitFor { (& $outText) -notmatch "Estrofa sintetica" }) "Esc deberia quitar el texto de la salida (salida: $(& $outText))"
    Check (& $waitFor { (& $badge) -eq "LISTO" }) "tras Esc: LISTO (decia: $(& $badge))"
    Press $cws "E" -Ctrl
    Check (& $waitFor { (Eval $cws "document.querySelector('[data-testid=`"list-tab`"]').dataset.mode") -eq "edit" }) "Ctrl+E deberia pasar al modo EDITAR"
    Press $cws "Enter"; Press $cws "Right"; Start-Sleep -Milliseconds 400
    Check ((& $outText) -notmatch "Estrofa sintetica") "en modo EDITAR ninguna tecla debe proyectar"
    Press $cws "E" -Ctrl
    Check (& $waitFor { (Eval $cws "document.querySelector('[data-testid=`"list-tab`"]').dataset.mode") -eq "live" }) "Ctrl+E deberia volver al modo EN VIVO"
    $after = (Invoke-Query $cws "playlist.get" @{ id = $listId }).result
    Check ($after.revision -eq $revisionBefore) "cambiar de elemento no debe escribir: la revision de la lista cambio de $revisionBefore a $($after.revision)"
    $longTasks = @(); foreach ($d in ((Eval $cws "JSON.stringify(window.__longTasks)") | ConvertFrom-Json)) { $longTasks += [int]$d }
    $maxLong = 0; foreach ($d in $longTasks) { if ($d -gt $maxLong) { $maxLong = $d } }
    $result.tareasLargas = "$($longTasks.Count) tareas largas (>50 ms) en el panel durante las pulsaciones, la mayor de $maxLong ms"
    Check ($maxLong -lt 500) "el panel se atasco $maxLong ms seguidos (limite de la prueba 500 ms; presupuesto de una tarea ~50 ms)"
    if ($Shots) {
        $png = Invoke-Cdp $cws "Page.captureScreenshot" @{ format = "png" } 15000
        if ($png) { [IO.File]::WriteAllBytes((Join-Path $data "lista.png"), [Convert]::FromBase64String($png.result.data)); $result.captura = Join-Path $data "lista.png" }
    }
    $result.recorrido = "Enter, flecha, Ctrl+flecha (salta nota, faltante y pasaje sin resolver), misma cancion repetida, FIN, Negro/Live, recarga, Esc y Ctrl+E con teclas reales y la salida real"

    # 9. El registro no contiene la letra
    $logs = Get-ChildItem (Join-Path $data "logs") -Recurse -File -ErrorAction SilentlyContinue
    $leaks = @($logs | Where-Object { (Get-Content $_.FullName -Raw) -match "Estrofa sintetica|segunda linea sintetica|Cancion sintetica|Domingo sintetico|Ofrendas|diaconos" })
    Check ($leaks.Count -eq 0) "el registro de la aplicacion contiene letra, titulos o notas: $($leaks.Name -join ', ')"
    $result.logsSinLetra = "revisados $($logs.Count) archivo(s): ninguna palabra de la letra, titulos ni notas"
}
catch { $failures.Add("La prueba se interrumpio: $($_.Exception.Message)  [$($_.InvocationInfo.ScriptLineNumber)]") }
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
