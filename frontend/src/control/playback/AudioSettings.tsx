import { useState } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { AudioSettingsPayload } from "@/ipc/protocol";
import type { PlaybackController } from "./controller";

const FADE_FIELDS: { key: "trackFadeInMs" | "trackFadeOutMs" | "videoFadeInMs" | "videoFadeOutMs"; label: string }[] = [
  { key: "trackFadeInMs", label: "Fundido de entrada de las pistas (ms)" },
  { key: "trackFadeOutMs", label: "Fundido de salida de las pistas (ms)" },
  { key: "videoFadeInMs", label: "Fundido de entrada del video (ms)" },
  { key: "videoFadeOutMs", label: "Fundido de salida del video (ms)" },
];

/**
 * Ajustes de audio. Hay dos rutas físicas (la salida analógica de la PC o la interfaz USB hacia la mezcladora) que Windows ve como dos dispositivos. Las PISTAS usan el «dispositivo de la
 * iglesia» elegido aquí (por su id estable de Windows, no por su nombre); el audio del VIDEO sale siempre por el predeterminado de Windows (limitación de WebView2). La aplicación no cambia
 * nunca el predeterminado de Windows ni salta a otro dispositivo por su cuenta.
 */
export function AudioSettingsView({ controller, connected }: { controller: PlaybackController; connected: boolean }) {
  const audio = useStore(controller.store, (s) => s.audio);
  const testing = useStore(controller.store, (s) => s.testing);
  const [fades, setFades] = useState<Partial<Record<(typeof FADE_FIELDS)[number]["key"], string>>>({});
  if (!audio) return <p className="text-sm text-neutral-600">Leyendo los ajustes de audio…</p>;
  const { settings, devices, route } = audio;

  const save = (changes: Partial<AudioSettingsPayload>): void => { controller.saveSettings({ ...settings, ...changes }); };
  const knownIds = new Set(devices.map((d) => d.id));
  const missingChurch = settings.churchDeviceId !== null && !knownIds.has(settings.churchDeviceId);

  return (
    <div className="flex flex-col gap-3 rounded-md border bg-neutral-50 p-3" data-testid="audio-settings">
      <p className="text-sm text-neutral-700">
        Las <b>pistas</b> salen por el dispositivo de la iglesia; el audio del <b>video</b> sale por el predeterminado de Windows
        {route.defaultDeviceName ? <> («{route.defaultDeviceName}»)</> : null}. La aplicación no cambia el predeterminado de Windows.
      </p>

      <label className="flex flex-col gap-1 text-sm font-semibold">
        Dispositivo de audio de la iglesia (pistas)
        <select
          className="h-10 rounded-md border-2 bg-white px-2 font-normal" disabled={!connected} aria-label="Dispositivo de audio de la iglesia"
          value={settings.churchDeviceId ?? ""}
          onChange={(e) => { controller.setDevice(e.target.value === "" ? null : e.target.value); }}
        >
          <option value="">Usar el predeterminado de Windows{route.defaultDeviceName ? ` (${route.defaultDeviceName})` : ""}</option>
          {missingChurch && <option value={settings.churchDeviceId ?? ""}>{settings.churchDeviceName ?? "Dispositivo de la iglesia"} (NO conectado)</option>}
          {devices.map((d) => <option key={d.id} value={d.id}>{d.name}{d.isDefault ? " · predeterminado de Windows" : ""}</option>)}
        </select>
      </label>

      <div className="flex flex-wrap items-center gap-2">
        <Button size="sm" variant="outline" disabled={!connected || testing !== null} onClick={() => { controller.testSound("church"); }} title="Un tono suave y corto por el dispositivo de la iglesia">
          {testing === "church" ? "Sonando…" : "Probar sonido: dispositivo de la iglesia"}
        </Button>
        <Button size="sm" variant="outline" disabled={!connected || testing !== null} onClick={() => { controller.testSound("default"); }} title="Un tono suave y corto por el predeterminado de Windows (por donde sale el audio del video)">
          {testing === "default" ? "Sonando…" : "Probar sonido: predeterminado de Windows"}
        </Button>
      </div>

      <label className="flex items-center gap-2 text-sm">
        <input
          type="checkbox" disabled={!connected} checked={settings.resumeOnDeviceReturn}
          onChange={(e) => { save({ resumeOnDeviceReturn: e.target.checked }); }}
        />
        Si el MISMO dispositivo vuelve tras desconectarse, reanudar sola la pista que estaba sonando (apagado por defecto)
      </label>

      <div className="grid grid-cols-2 gap-2">
        {FADE_FIELDS.map(({ key, label }) => (
          <label key={key} className="flex flex-col gap-1 text-xs font-semibold">
            {label}
            <input
              type="number" min={0} max={5000} step={50} className="h-9 rounded-md border-2 px-2 font-normal" disabled={!connected}
              value={fades[key] ?? String(settings[key])}
              onChange={(e) => { setFades((f) => ({ ...f, [key]: e.target.value })); }}
              onBlur={() => {
                const raw = fades[key];
                if (raw === undefined) return;
                const n = Math.round(Number(raw));
                setFades((f) => ({ ...f, [key]: undefined }));
                if (Number.isFinite(n) && n >= 0 && n <= 5000 && n !== settings[key]) save({ [key]: n });
              }}
            />
          </label>
        ))}
      </div>
    </div>
  );
}
