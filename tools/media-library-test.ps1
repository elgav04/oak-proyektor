# Prueba REAL de la biblioteca de medios sobre la APLICACION REAL (compilacion Debug), con la depuracion remota de WebView2 SOLO para esta prueba. Se usa la INTERFAZ REAL del panel
# (pestana «Biblioteca»: pegar la ruta, Importar, filas, insignias, miniaturas, Fondo, borrar, verificar). La salida se abre en una VENTANA NORMAL (nunca en el proyector). Los datos van a una
# carpeta temporal (OAK_DATA_DIR). NINGUN medio de terceros: el video y la imagen se GENERAN dentro de la propia salida (MediaRecorder / canvas) y el audio es un WAV sintetico (seno de 440 Hz).
#
# Comprueba: importar un MP4 real (copia con SHA-256 identico al original, nombre en disco = hash, verificacion en el reproductor del navegador, miniatura que SE CARGA); nombres con caracteres
# raros; duplicado por contenido; imagen y audio; formatos no soportados y archivos corruptos o vacios con su mensaje; usar un video como fondo (la salida lo reproduce desde la copia local);
# no poder borrar el fondo en uso; borrar quita fila, archivo y miniatura; «Verificar biblioteca» marca el archivo que desaparecio; el panel no se atasca (tareas largas) y el registro de la
# aplicacion no contiene nombres ni rutas del operador.
#   powershell -File tools\media-library-test.ps1 [-KeepData]
param(
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$protoV = [regex]::Match((Get-Content (Join-Path $root "frontend\src\ipc\protocol.ts") -Raw), 'PROTOCOL_VERSION\s*=\s*(\d+)').Groups[1].Value
if (-not $protoV) { throw 'No se pudo leer PROTOCOL_VERSION de frontend\src\ipc\protocol.ts' }
$exe = Get-ChildItem (Join-Path $root "backend\src\OakProyektor.App\bin\Debug") -Recurse -Filter OakProyektor.App.exe | Select-Object -First 1
if (-not $exe) { throw "Compila en Debug primero (npm run build en frontend/ y dotnet build)." }

$data = Join-Path $env:TEMP ("oak-medialib-" + [guid]::NewGuid().ToString("N"))
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
$SECRET = "SECRETO-OPERADOR"
try {
    $control = $null
    for ($i = 0; $i -lt 40 -and -not $control; $i++) { Start-Sleep -Milliseconds 500; $control = Get-Targets "control" | Where-Object { $_.type -eq "page" -and $_.url -match "index\.html" } | Select-Object -First 1 }
    if (-not $control) { throw "No aparecio el panel" }
    $cws = Connect-Cdp $control.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $list = Eval $cws '(async () => { const id = crypto.randomUUID(); return await new Promise((resolve) => { window.external.receiveMessage((raw) => { const m = JSON.parse(raw); if (m.id === id) resolve(JSON.stringify(m.payload)); }); window.external.sendMessage(JSON.stringify({ v:@@V@@, id, kind: "query", type: "display.list", payload: {} })); }); })()'
    $panelId = ((($list | ConvertFrom-Json).result.displays) | Where-Object { $_.hostsPanel } | Select-Object -First 1).id
    [void](Eval $cws ("window.external.sendMessage(JSON.stringify({v:@@V@@,id:crypto.randomUUID(),kind:'command',type:'output.open',payload:{displayId:" + ($panelId | ConvertTo-Json -Compress) + "}}));true"))
    $output = $null
    for ($i = 0; $i -lt 40 -and -not $output; $i++) { Start-Sleep -Milliseconds 500; $output = Get-Targets "output" | Where-Object { $_.type -eq "page" -and $_.url -match "output\.html" } | Select-Object -First 1 }
    if (-not $output) { throw "No aparecio la ventana de salida" }
    $ows = Connect-Cdp $output.webSocketDebuggerUrl
    Start-Sleep -Seconds 2
    $result.chromium = Eval $ows "navigator.userAgent"
    Send-Command $cws "output.setMaster" @{ master = "live" }

    # ───────── Material generado (nada de terceros) ─────────
    $src = Join-Path $data "origen"; New-Item -ItemType Directory $src | Out-Null
    Write-Host "Generando material de prueba (video H.264 de 3 s, imagen y audio)..."
    $video = Join-Path $src "$SECRET video.mp4"
    New-TestVideo $ows $video 3 20
    $weird = Join-Path $src "Anuncio 100% #1 & mas ñandu $SECRET.mp4"
    Copy-Item $video (Join-Path $src "copia exacta $SECRET.mp4")
    Copy-Item $video $weird -ErrorAction SilentlyContinue
    $image = Join-Path $src "$SECRET fondo.png"; New-TestImage $ows $image
    $wav = Join-Path $src "$SECRET pista.wav"
    $rate = 22050; $samples = $rate * 1
    $bytes = New-Object byte[] (44 + $samples * 2)
    [Text.Encoding]::ASCII.GetBytes("RIFF").CopyTo($bytes, 0); [BitConverter]::GetBytes([int](36 + $samples * 2)).CopyTo($bytes, 4); [Text.Encoding]::ASCII.GetBytes("WAVEfmt ").CopyTo($bytes, 8)
    [BitConverter]::GetBytes([int]16).CopyTo($bytes, 16); [BitConverter]::GetBytes([int16]1).CopyTo($bytes, 20); [BitConverter]::GetBytes([int16]1).CopyTo($bytes, 22); [BitConverter]::GetBytes([int]$rate).CopyTo($bytes, 24)
    [BitConverter]::GetBytes([int]($rate * 2)).CopyTo($bytes, 28); [BitConverter]::GetBytes([int16]2).CopyTo($bytes, 32); [BitConverter]::GetBytes([int16]16).CopyTo($bytes, 34)
    [Text.Encoding]::ASCII.GetBytes("data").CopyTo($bytes, 36); [BitConverter]::GetBytes([int]($samples * 2)).CopyTo($bytes, 40)
    for ($i = 0; $i -lt $samples; $i++) { [BitConverter]::GetBytes([int16](8000 * [Math]::Sin(2 * [Math]::PI * 440 * $i / $rate))).CopyTo($bytes, 44 + 2 * $i) }
    [IO.File]::WriteAllBytes($wav, $bytes)
    $avi = Join-Path $src "$SECRET viejo.avi"; [IO.File]::WriteAllBytes($avi, (New-Object byte[] 4096))
    $fake = Join-Path $src "$SECRET falso.mp4"; $rnd = New-Object byte[] 6000; (New-Object Random 7).NextBytes($rnd); [IO.File]::WriteAllBytes($fake, $rnd)
    $empty = Join-Path $src "$SECRET vacio.mp4"; [IO.File]::WriteAllBytes($empty, (New-Object byte[] 0))

    # ───────── Ayudas para manejar la INTERFAZ REAL del panel ─────────
    [void](Eval $cws '(() => { window.__longTasks = []; window.__marks = []; try { new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__longTasks.push({ s: Math.round(e.startTime), d: Math.round(e.duration) }); }).observe({ entryTypes: ["longtask"] }); } catch (e) {} return true; })()')
    [void](Eval $cws '(() => { const b = [...document.querySelectorAll("nav button")].find((x) => x.textContent.startsWith("Biblioteca")); b.click(); return true; })()')
    Start-Sleep -Seconds 1
    function Panel($js) { Eval $cws $js 10000 }
    function Rows { $all = (Panel 'JSON.stringify([...document.querySelectorAll("[data-testid=media-row]")].map((r) => ({ id: r.dataset.mediaId, text: r.innerText, badge: r.querySelector("[data-testid=media-badge]").innerText, img: (r.querySelector("img") || {}).src || null, loaded: !!(r.querySelector("img") && r.querySelector("img").complete && r.querySelector("img").naturalWidth > 0) })))') | ConvertFrom-Json; foreach ($r in $all) { $r } }
    function Notice { (Panel '(document.querySelector("[data-testid=media-notice]") || {}).innerText || ""') }
    function Import-File([string]$path, [string]$name = "") {
        $p = $path.Replace('\', '\\').Replace("'", "\'"); $n = $name.Replace("'", "\'")
        [void](Panel "(() => { const set = (el, v) => { Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set.call(el, v); el.dispatchEvent(new Event('input', { bubbles: true })); }; set(document.querySelector('input[aria-label=\""Ruta completa del archivo\""]'), '$p'); set(document.querySelector('input[aria-label=\""Nombre (opcional)\""]'), '$n'); return true; })()")
        Start-Sleep -Milliseconds 200
        [void](Panel '(() => { [...document.querySelectorAll("button")].find((b) => b.textContent.trim() === "Importar").click(); return true; })()')
        $end = [DateTime]::UtcNow.AddSeconds(40)
        while ([DateTime]::UtcNow -lt $end) { Start-Sleep -Milliseconds 300; $nt = Notice; if ($nt -and -not [bool](Panel '!!document.querySelector("[data-testid=media-progress]")')) { return $nt } }
        return "(sin resultado)"
    }
    function Wait-Rows([scriptblock]$cond, [int]$seconds = 25) { $end = [DateTime]::UtcNow.AddSeconds($seconds); while ([DateTime]::UtcNow -lt $end) { $r = @(Rows); if (& $cond $r) { return ,$r }; Start-Sleep -Milliseconds 300 }; return ,@(Rows) }
    function Dismiss { [void](Panel '(() => { const b = [...document.querySelectorAll("[data-testid=media-notice] button")].find((x) => x.textContent.includes("Cerrar")); if (b) b.click(); return true; })()') }

    # ───────── 1. Importar un MP4 real ─────────
    Write-Host "Importar un MP4 (generado)"; [void](Panel 'window.__marks.push(["Importar un MP4 (generado)", performance.now()])')
    $notice = Import-File $video
    Check ($notice -match "Importado") "el panel confirma la importacion ($notice)"
    $rows = Wait-Rows { param($r) $r.Count -ge 1 -and $r[0].badge -eq "Listo" -and $r[0].loaded }
    Check ($rows.Count -eq 1) "hay UNA fila"
    Check ($rows[0].badge -eq "Listo") "el navegador del panel lo verifico: «Listo» (decodifico un fotograma real)"
    Check ([bool]$rows[0].loaded) "la miniatura SE CARGA (naturalWidth > 0), generada por el reproductor oculto"
    $stored = @(Get-ChildItem (Join-Path $data "media\files") -Recurse -File)
    Check ($stored.Count -eq 1) "una sola copia en media\files"
    $hash = (Get-FileHash $video -Algorithm SHA256).Hash.ToLowerInvariant()
    Check ($stored[0].Name -eq "$hash.mp4") "el nombre en disco ES el SHA-256 del original ($($stored[0].Name.Substring(0, 12))…)"
    Check ((Get-FileHash $stored[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -eq $hash) "la copia es identica byte a byte"
    Check (Test-Path $video) "el original no se toco"
    Check (@(Get-ChildItem (Join-Path $data "media\tmp") -File).Count -eq 0) "no queda ninguna copia a medias en tmp"
    Check ([bool]($rows[0].text -match "H\.264")) "se muestra el codec H.264"
    Dismiss

    # ───────── 2. Duplicado por contenido y nombre raro ─────────
    Write-Host "Duplicado y nombre raro"; [void](Panel 'window.__marks.push(["Duplicado y nombre raro", performance.now()])')
    $notice = Import-File (Join-Path $src "copia exacta $SECRET.mp4")
    Check ($notice -match "ya está en la biblioteca como") "el mismo contenido con otro nombre es un DUPLICADO ($notice)"
    Check (@(Rows).Count -eq 1) "no se creo otra fila"
    Check (@(Get-ChildItem (Join-Path $data "media\files") -Recurse -File).Count -eq 1) "ni otro archivo"
    Dismiss

    # ───────── 3. Imagen y audio ─────────
    Write-Host "Imagen y audio"; [void](Panel 'window.__marks.push(["Imagen y audio", performance.now()])')
    Check ((Import-File $image) -match "Importado") "la imagen se importa"
    Check ((Import-File $wav "Pista de prueba") -match "Importado") "el audio WAV se importa con nombre propio"
    Dismiss
    $rows = Wait-Rows { param($r) $r.Count -eq 3 -and @($r | Where-Object { $_.badge -eq "Listo" }).Count -eq 3 }
    Check ($rows.Count -eq 3 -and @($rows | Where-Object { $_.badge -eq "Listo" }).Count -eq 3) "las 3 filas quedan «Listo»"
    $imageRow = @($rows | Where-Object { $_.text -cmatch "Imagen ·" })[0]
    $audioRow = @($rows | Where-Object { $_.text -cmatch "Audio ·" })[0]
    Check ([bool]$imageRow.loaded) "la imagen tiene miniatura que se carga"
    Check ($null -eq $audioRow.img) "el audio lleva icono, no miniatura"
    Check ([bool]($audioRow.text -match "0:01")) "la duracion real del audio es 0:01"
    Check ($audioRow.text -match "Pista de prueba") "conserva el nombre que se le dio"

    # ───────── 4. Formatos no soportados y archivos malos ─────────
    Write-Host "No soportados"; [void](Panel 'window.__marks.push(["No soportados", performance.now()])')
    $n1 = Import-File $avi; Check (($n1 -match "no soportado") -and ($n1 -match "MP4") -and ($n1 -notmatch $SECRET)) "un .avi dice que no se soporta y como convertirlo, sin citar el nombre ($n1)"; Dismiss
    $n2 = Import-File $fake; Check (($n2 -match "no es válido|no está soportado|no se puede usar") -and ($n2 -notmatch $SECRET)) "un .mp4 que no lo es se rechaza con su mensaje ($n2)"; Dismiss
    $n3 = Import-File $empty; Check (($n3 -match "vacío") -and ($n3 -notmatch $SECRET)) "un archivo vacio se rechaza ($n3)"; Dismiss
    $n4 = Import-File (Join-Path $src "no existe $SECRET.mp4"); Check (($n4 -match "no existe|No se puede importar") -and ($n4 -notmatch $SECRET)) "una ruta inexistente se rechaza ($n4)"; Dismiss
    Check (@(Rows).Count -eq 3) "ninguno de los rechazados entro en la biblioteca"
    Check (@(Get-ChildItem (Join-Path $data "media\files") -Recurse -File).Count -eq 3 -and @(Get-ChildItem (Join-Path $data "media\tmp") -File).Count -eq 0) "ni dejo archivos sueltos"

    # ───────── 5. Usar como fondo: la salida lo reproduce desde la COPIA local ─────────
    Write-Host "Fondo desde la biblioteca"; [void](Panel 'window.__marks.push(["Fondo desde la biblioteca", performance.now()])')
    [void](Panel '(() => { const row = [...document.querySelectorAll("[data-testid=media-row]")].find((r) => r.innerText.includes("Video")); [...row.querySelectorAll("button")].find((b) => b.textContent === "Fondo").click(); return true; })()')
    $playing = $false; for ($i = 0; $i -lt 40 -and -not $playing; $i++) { Start-Sleep -Milliseconds 300; $playing = [bool](Eval $ows '(() => { const v = document.querySelector("video"); return !!v && v.readyState >= 2 && v.currentTime > 0; })()') }
    Check $playing "la salida reproduce el video de fondo"
    $srcUrl = Eval $ows 'document.querySelector("video").currentSrc'
    Check (($srcUrl -match "/media/files/") -and ($srcUrl -notmatch "origen") -and ($srcUrl -notmatch [regex]::Escape($SECRET))) "lo reproduce desde la COPIA de la biblioteca, no del origen ($($srcUrl.Substring($srcUrl.Length - 24)))"

    # ───────── 6. El fondo en uso no se borra; borrar quita fila, archivo y miniatura ─────────
    Write-Host "Borrar"; [void](Panel 'window.__marks.push(["Borrar", performance.now()])')
    [void](Panel '(() => { const row = [...document.querySelectorAll("[data-testid=media-row]")].find((r) => r.innerText.includes("Video")); [...row.querySelectorAll("button")].find((b) => (b.getAttribute("aria-label") || "").startsWith("Borrar")).click(); return true; })()')
    Start-Sleep -Milliseconds 300
    [void](Panel '(() => { [...document.querySelectorAll("button")].find((b) => b.textContent.includes("Sí, borrar")).click(); return true; })()')
    Start-Sleep -Seconds 2
    $failure = Panel '(document.querySelector("[role=alert]") || {}).innerText || ""'
    Check (($failure -match "fondo") -and (@(Rows).Count -eq 3)) "borrar el fondo en uso se RECHAZA con su motivo y la fila sigue ($($failure.Substring(0, [Math]::Min(70, $failure.Length))))"
    Check (@(Get-ChildItem (Join-Path $data "media\files") -Recurse -File).Count -eq 3) "y su archivo sigue en disco"
    $thumbsBefore = @(Get-ChildItem (Join-Path $data "media\thumbs") -File).Count
    [void](Panel '(() => { const row = [...document.querySelectorAll("[data-testid=media-row]")].find((r) => r.innerText.includes("Imagen")); [...row.querySelectorAll("button")].find((b) => (b.getAttribute("aria-label") || "").startsWith("Borrar")).click(); return true; })()')
    Start-Sleep -Milliseconds 300
    [void](Panel '(() => { [...document.querySelectorAll("button")].find((b) => b.textContent.includes("Sí, borrar")).click(); return true; })()')
    $rows = Wait-Rows { param($r) $r.Count -eq 2 } 10
    Check ($rows.Count -eq 2) "borrar la imagen quita su fila"
    Check (@(Get-ChildItem (Join-Path $data "media\files") -Recurse -File).Count -eq 2) "y su archivo del disco"
    Check (@(Get-ChildItem (Join-Path $data "media\thumbs") -File).Count -eq $thumbsBefore - 1) "y su miniatura"

    # ───────── 7. Verificar biblioteca: el archivo que desaparece se marca ─────────
    Write-Host "Verificar biblioteca"; [void](Panel 'window.__marks.push(["Verificar biblioteca", performance.now()])')
    $wavStored = Get-ChildItem (Join-Path $data "media\files") -Recurse -File -Filter "*.wav" | Select-Object -First 1
    Remove-Item $wavStored.FullName -Force
    [void](Panel '(() => { [...document.querySelectorAll("button")].find((b) => b.textContent.includes("Verificar biblioteca")).click(); return true; })()')
    $rows = Wait-Rows { param($r) @($r | Where-Object { $_.badge -eq "Falta el archivo" }).Count -eq 1 } 10
    Check (@($rows | Where-Object { $_.badge -eq "Falta el archivo" }).Count -eq 1) "«Verificar biblioteca» marca el archivo que desaparecio («Falta el archivo»)"
    Check (@($rows | Where-Object { $_.badge -eq "Listo" }).Count -eq 1) "y el otro sigue «Listo»"

    # ───────── 8. Panel sin bloqueos y registro sin nombres ─────────
    Write-Host "Panel y registro"
    $long = (Panel 'JSON.stringify(window.__longTasks)') | ConvertFrom-Json
    $over = @($long | Where-Object { $_.d -ge 100 })
    Check ($over.Count -eq 0) "ninguna tarea del hilo principal del panel supero 100 ms durante todas las importaciones (tareas largas: $(@($long).Count), maxima $(if (@($long).Count) { ($long | Measure-Object d -Maximum).Maximum } else { 0 }) ms)"
    foreach ($lt in $long) { $near = ((Panel ("JSON.stringify(window.__marks.filter((m) => m[1] <= " + $lt.s + ").slice(-1))")) | ConvertFrom-Json); Write-Host ("        tarea larga {0} ms en t={1} tras la marca: {2}" -f $lt.d, $lt.s, (@($near)[0] -join " @ ")) }
    $logText = (Get-ChildItem (Join-Path $data "logs") -Filter *.log -ErrorAction SilentlyContinue | ForEach-Object { Get-Content $_.FullName -Raw -Encoding UTF8 }) -join "`n"
    Check ($logText.Length -gt 0) "hay registro de la aplicacion"
    Check ($logText -notmatch $SECRET -and $logText -notmatch "ñandu" -and $logText -notmatch "Anuncio 100%") "el registro NO contiene nombres de archivo del operador"
    Check ($logText -notmatch [regex]::Escape($src)) "ni la ruta de origen"
    Check ($logText -match "terminada: medio \d+") "pero si los ids y conteos de lo importado"

    $result.biblioteca = "filas: $(@(Rows).Count); archivos: $(@(Get-ChildItem (Join-Path $data 'media\files') -Recurse -File).Count); miniaturas: $(@(Get-ChildItem (Join-Path $data 'media\thumbs') -File).Count)"
    $result.FALLOS = if ($fails.Count -eq 0) { "ninguno" } else { $fails -join " || " }
}
finally {
    $result.GetEnumerator() | ForEach-Object { "{0}: {1}" -f $_.Key, $_.Value }
    if ($proc -and -not $proc.HasExited) { Stop-Process -Id $proc.Id -Force }   # solo el PID que arrancamos
    Start-Sleep -Seconds 1
    if ($KeepData) { Write-Host "Datos conservados en $data (logs en $data\logs)" } else { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }
}
if ($fails.Count -gt 0) { exit 1 }
