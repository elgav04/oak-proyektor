import { useEffect, useState } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { OutputStateChangedPayload, PlaybackTarget } from "@/ipc/protocol";
import { AudioSettingsView } from "./AudioSettings";
import type { PlaybackController } from "./controller";
import { TRACK_PHASE_LABEL, defaultChangeMessage, foregroundBadge, foregroundStatus, formatClock, routeSummary } from "./model";

const TONE: Record<string, string> = { green: "bg-green-100 text-green-900", amber: "bg-amber-100 text-amber-900", red: "bg-red-100 text-red-900", grey: "bg-neutral-200 text-neutral-700" };

/** Barra de avance: arrastrar manda «buscar» con el último valor (≤ 1 cada 100 ms); al soltar, el valor exacto. El tiempo mostrado es cosmético: la verdad es la salida (o el motor de audio). */
function SeekBar({ label, positionMs, durationMs, disabled, onSeek, onCommit }: { label: string; positionMs: number; durationMs: number; disabled: boolean; onSeek: (ms: number) => void; onCommit: (ms: number) => void }) {
  const [scrub, setScrub] = useState<number | null>(null);
  const shown = scrub ?? Math.min(positionMs, durationMs || positionMs);
  return (
    <div className="flex min-w-0 flex-1 items-center gap-2">
      <span className="w-14 shrink-0 text-right text-xs tabular-nums">{formatClock(shown)}</span>
      <input
        type="range" aria-label={label} min={0} max={Math.max(1, durationMs)} step={250} value={Math.min(shown, Math.max(1, durationMs))}
        disabled={disabled || durationMs <= 0} className="min-w-0 flex-1"
        onChange={(e) => { const v = Number(e.target.value); setScrub(v); onSeek(v); }}
        onPointerUp={() => { if (scrub !== null) onCommit(scrub); setScrub(null); }}
        onKeyUp={() => { if (scrub !== null) onCommit(scrub); setScrub(null); }}
        onBlur={() => { setScrub(null); }}
      />
      <span className="w-14 shrink-0 text-xs tabular-nums">{formatClock(durationMs)}</span>
    </div>
  );
}

function VolumeBar({ label, valuePct, disabled, onChange }: { label: string; valuePct: number; disabled: boolean; onChange: (pct: number) => void }) {
  return (
    <label className="flex shrink-0 items-center gap-2 text-xs font-semibold">
      <span aria-hidden="true">🔊</span>
      <input type="range" aria-label={label} min={0} max={100} step={1} value={valuePct} disabled={disabled} className="w-28" onChange={(e) => { onChange(Number(e.target.value)); }} />
      <span className="w-9 tabular-nums">{valuePct}%</span>
    </label>
  );
}

function Badge({ tone, children }: { tone: keyof typeof TONE; children: string }) {
  return <span className={`shrink-0 rounded-full px-2 py-0.5 text-xs font-bold ${TONE[tone] ?? ""}`} data-testid="player-badge">{children}</span>;
}

/**
 * Reproductor fijo, visible en todas las pestañas: el video en primer plano (lo reproduce la salida) y la pista de audio (la reproduce el motor de C#), con «Silencio» siempre a la mano.
 * Sin estado de negocio: lo que se ve y suena se DERIVA del estado que confirma el backend (`output.stateChanged` y `audio.stateChanged`), así que tras recargar el panel se recupera solo.
 * Muestra SIEMPRE por dónde sale cada audio y avisa de forma visible si no coinciden.
 */
export function PlayerBar({ controller, connected, output }: { controller: PlaybackController; connected: boolean; output: OutputStateChangedPayload | null }) {
  const s = useStore(controller.store);
  const [settingsOpen, setSettingsOpen] = useState(false);
  useEffect(() => { if (connected) void controller.init(); }, [controller, connected]);

  const fg = output?.state.foreground ?? null;
  const status = foregroundStatus(output);
  const audio = s.audio;
  const track = audio?.track ?? null;
  const route = audio ? routeSummary(audio.route) : null;
  const change = audio?.defaultChange && audio.defaultChange.at !== s.dismissedDefaultChange ? audio.defaultChange : null;
  const nothing = fg === null && track === null;

  const fgProgress = fg && s.fgProgress?.id === fg.id ? s.fgProgress : null;
  const fgDuration = fgProgress?.durationMs ?? 0;
  const trackPos = track ? (track.phase === "playing" && s.trackProgress ? s.trackProgress.positionMs : track.positionMs) : 0;
  const trackDuration = track ? Math.max(track.durationMs, s.trackProgress?.durationMs ?? 0) : 0;
  const fgHidden = status.kind === "hidden";

  const transport = (target: PlaybackTarget, playing: boolean, resumable: boolean, disabledPlay: boolean, playTitle: string) => (
    <div className="flex shrink-0 items-center gap-2">
      {playing
        ? <Button size="sm" disabled={!connected} aria-label={target === "foreground" ? "Pausar el video" : "Pausar la pista"} onClick={() => { controller.pause(target); }}>⏸ Pausar</Button>
        : <Button size="sm" disabled={!connected || disabledPlay || !resumable} aria-label={target === "foreground" ? "Reproducir el video" : "Reproducir la pista"} title={playTitle} onClick={() => { controller.resume(target); }}>▶ Reproducir</Button>}
      <Button size="sm" variant="outline" disabled={!connected} aria-label={target === "foreground" ? "Detener el video" : "Detener la pista"} onClick={() => { controller.stop(target); }}>⏹ Detener</Button>
    </div>
  );

  return (
    <section aria-label="Reproductor" data-testid="player-bar" className="flex flex-col gap-2 rounded-lg border-2 p-3">
      <div className="flex flex-wrap items-center gap-3">
        <h2 className="text-base font-semibold">Reproductor</h2>
        <Button
          size="sm" variant="destructive" disabled={!connected} aria-label="Silencio: calla las pistas y pausa el video"
          title="Calla las pistas y pausa el video en primer plano (Ctrl+Alt+Shift+S). Solo calla: nunca reanuda."
          onClick={() => { controller.silence(); }}
        >
          🔇 Silencio
        </Button>
        {audio && <VolumeBar label="Volumen maestro" valuePct={audio.settings.masterVolumePct} disabled={!connected} onChange={(pct) => { controller.setMasterVolume(pct); }} />}
        <span className="text-xs text-neutral-500">Ctrl+Alt+Shift+S también calla aunque este panel se atasque</span>
        <Button size="sm" variant="outline" className="ml-auto" aria-expanded={settingsOpen} onClick={() => { setSettingsOpen((o) => !o); }}>Ajustes de audio</Button>
      </div>

      {route && (
        <p className="text-sm" data-testid="player-route">
          <b>Pistas:</b> {route.tracks} · <b>Video:</b> {route.video} <span className="text-neutral-500">(predeterminado de Windows)</span>
        </p>
      )}
      {route?.warnings.map((w) => <p key={w} role="alert" className="rounded-md bg-red-100 p-2 text-sm font-semibold text-red-900" data-testid="player-warning">⚠ {w}</p>)}
      {change && (
        <div role="alert" className="flex items-center gap-2 rounded-md bg-amber-100 p-2 text-sm text-amber-900" data-testid="player-default-change">
          <span className="flex-1">⚠ {defaultChangeMessage(change.fromName, change.toName)}</span>
          <Button size="sm" variant="outline" onClick={() => { controller.dismissDefaultChange(); }}>Entendido</Button>
        </div>
      )}
      {s.failure && (
        <div role="alert" className="flex items-center gap-2 rounded-md bg-red-100 p-2 text-sm text-red-900" data-testid="player-failure">
          <span className="flex-1">{s.failure}</span>
          <Button size="sm" variant="outline" onClick={() => { controller.dismissFailure(); }}>Cerrar</Button>
        </div>
      )}
      {s.notice && (
        <div role="status" className="flex items-center gap-2 rounded-md bg-neutral-100 p-2 text-sm">
          <span className="flex-1">{s.notice}</span>
          <Button size="sm" variant="outline" onClick={() => { controller.dismissNotice(); }}>Cerrar</Button>
        </div>
      )}

      {fg && (
        <div className="flex flex-wrap items-center gap-3 rounded-md border p-2" data-testid="player-foreground">
          <Badge tone={status.kind === "onScreen" ? "green" : status.kind === "hidden" ? "amber" : "grey"}>{foregroundBadge(status)}</Badge>
          <span className="max-w-56 truncate font-semibold" title={fg.fileName}>🎬 {fg.fileName}</span>
          {transport("foreground", status.kind === "onScreen", true, fgHidden, fgHidden ? "La salida no muestra el video ahora (Negro, Texto limpio, Logo o en pausa): vuelve a «En vivo»" : "Reproducir")}
          <SeekBar
            label="Posición del video" positionMs={fgProgress?.positionMs ?? 0} durationMs={fgDuration} disabled={!connected}
            onSeek={(ms) => { controller.seek("foreground", ms); }} onCommit={(ms) => { controller.seekNow("foreground", ms); }}
          />
          <VolumeBar label="Volumen del video" valuePct={fg.clipVolumePct} disabled={!connected} onChange={(pct) => { controller.setClipVolume("foreground", pct); }} />
          <Button size="sm" variant="outline" aria-pressed={fg.muted} disabled={!connected} onClick={() => { controller.setMuted(!fg.muted); }}>{fg.muted ? "🔈 Con sonido" : "🔇 Silenciar video"}</Button>
        </div>
      )}

      {track && (
        <div className="flex flex-wrap items-center gap-3 rounded-md border p-2" data-testid="player-track">
          <Badge tone={track.phase === "playing" ? "green" : track.phase === "deviceLost" || track.phase === "error" ? "red" : "grey"}>{TRACK_PHASE_LABEL[track.phase]}</Badge>
          <span className="max-w-56 truncate font-semibold" title={s.trackName ?? ""}>♪ {s.trackName ?? "Pista"}</span>
          {transport("track", track.phase === "playing", true, false, track.phase === "deviceLost" ? "Reanudar por el dispositivo de la iglesia (si está conectado)" : "Reproducir")}
          <SeekBar
            label="Posición de la pista" positionMs={trackPos} durationMs={trackDuration} disabled={!connected}
            onSeek={(ms) => { controller.seek("track", ms); }} onCommit={(ms) => { controller.seekNow("track", ms); }}
          />
          <VolumeBar label="Volumen de la pista" valuePct={track.volumePct} disabled={!connected} onChange={(pct) => { controller.setClipVolume("track", pct); }} />
          {track.errorMessage && <p className="basis-full text-sm font-semibold text-red-900" role="alert">{track.errorMessage}</p>}
        </div>
      )}

      {nothing && <p className="text-sm text-neutral-600">Nada en reproducción. Usa «▶ En pantalla» (videos) o «▶ Pista» (audio) en la pestaña Biblioteca. Las pistas no se cortan con Negro, Texto limpio, Logo ni con la pausa de la salida.</p>}

      {settingsOpen && <AudioSettingsView controller={controller} connected={connected} />}
    </section>
  );
}
