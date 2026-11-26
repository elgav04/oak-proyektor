import { createStore, type StoreApi } from "zustand/vanilla";
import type { IpcClient } from "@/ipc/client";
import type { AlertListPayload, AlertMode, AlertSettingsPayload, AlertTheme, SavedAlertDto } from "@/ipc/protocol";
import { ALERT_MAX_CHARS, ALERT_MAX_PINNED, alertSettingsProblem, type AlertFit, type AlertMeasurer } from "./model";
import { DEFAULT_ALERT_SHORTCUTS, loadBindings, saveBindings, validateCombo, type AlertShortcutAction, type AlertShortcutBindings, type BindingsStorage } from "./alertShortcuts";

/** Lo que el controlador necesita del cliente IPC (así se prueba con un backend falso sobre el cliente real). */
export type AlertApi = Pick<IpcClient, "send" | "request" | "subscribe">;

export interface AlertState {
  /** Lo que el operador está escribiendo (estado de VISTA: el texto vive en el panel hasta que se muestra). */
  draft: string;
  mode: AlertMode;
  durationSec: number;
  /** Ajustes que confirma el backend (null hasta que se leen). */
  settings: AlertSettingsPayload | null;
  /** Cómo quedaría la alerta en la cinta (medición real, antes de mostrar). */
  fit: AlertFit | null;
  pinned: SavedAlertDto[];
  recent: SavedAlertDto[];
  /** El texto cargado desde una fila de la lista (solo para marcarla); NO significa que esté en pantalla. */
  loadedFrom: number | null;
  listError: string | null;
  /** Por qué NO se mostró una alerta guardada con ▶ (no cabe en la cinta o el desplazamiento está desactivado). `canTicker`: se puede ofrecer mostrarla con desplazamiento. */
  savedNotice: { alertId: number; message: string; canTicker: boolean } | null;
  settingsError: string | null;
  shortcuts: AlertShortcutBindings;
  shortcutError: string | null;
  /** Se sube al pedir ir al cuadro (Ctrl+Mayús+K): la barra enfoca el campo al verlo cambiar. */
  focusTick: number;
}

export interface AlertController {
  store: StoreApi<AlertState>;
  /** Lee los ajustes y la lista (al abrir la página y al reconectar). */
  init(): Promise<void>;
  refreshList(): Promise<void>;
  setDraft(text: string): void;
  setMode(mode: AlertMode): void;
  setDuration(sec: number): void;
  /** Carga el texto de una fila en el cuadro. NO lo muestra. */
  loadFrom(alert: SavedAlertDto): void;
  /** ¿Se puede mostrar lo escrito? Motivo si no. */
  showProblem(): string | null;
  /** Manda `alert.show`. No marca nada como «en pantalla»: eso lo dice el backend en el estado de la salida. Devuelve false si no se mandó. */
  show(): boolean;
  /**
   * Muestra una alerta guardada con los ajustes actuales de modo y duración (botón ▶ de la fila), con la MISMA medición que el cuadro: si no cabe en la cinta NO se manda y
   * `savedNotice` dice por qué (y ofrece el desplazamiento si está permitido). Nunca cambia de modo en silencio. Devuelve true si se mandó.
   */
  showSaved(alert: SavedAlertDto): boolean;
  /** El operador aceptó la oferta de `savedNotice`: cambia a desplazamiento (explícito) y la muestra. false si el desplazamiento está desactivado. */
  showSavedAsTicker(alert: SavedAlertDto): boolean;
  dismissSavedNotice(): void;
  hide(): void;
  pin(alert: SavedAlertDto, pinned: boolean): void;
  saveDraftPinned(): void;
  remove(alert: SavedAlertDto): void;
  saveSettings(settings: AlertSettingsPayload): void;
  requestFocus(): void;
  setShortcut(action: AlertShortcutAction, combo: string): string | null;
  resetShortcuts(): void;
  dispose(): void;
}

export interface AlertControllerOptions {
  api: AlertApi;
  /** Medidor del layout real. Sin él (pruebas sin DOM) todo «cabe». */
  measurer?: AlertMeasurer | null;
  shortcutStorage?: BindingsStorage | null;
  /** Cuánto esperar antes de releer la lista tras mostrar (el uso se anota en segundo plano en el backend). */
  listRefreshDelayMs?: number;
}

const FALLBACK_THEME: AlertTheme | null = null;

export function createAlertController(options: AlertControllerOptions): AlertController {
  const { api, measurer = null, shortcutStorage = null, listRefreshDelayMs = 900 } = options;
  const store = createStore<AlertState>(() => ({
    draft: "", mode: "band", durationSec: 30, settings: null, fit: null, pinned: [], recent: [], loadedFrom: null, listError: null, savedNotice: null, settingsError: null,
    shortcuts: loadBindings(shortcutStorage), shortcutError: null, focusTick: 0,
  }));
  const patch = (changes: Partial<AlertState>): void => { store.setState(changes); };
  let disposed = false;
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;
  let listToken = 0;

  const theme = (): AlertTheme | null => store.getState().settings?.theme ?? FALLBACK_THEME;

  /** Mide la alerta con el layout real: unas pocas lecturas de `offsetHeight` sobre un nodo pequeño (≤ 9), a lo sumo una por pulsación. En modo desplazamiento no hay nada que medir. */
  const refit = (): void => {
    const s = store.getState();
    const t = theme();
    if (!measurer || !t || s.mode === "ticker" || s.draft.trim().length === 0) { if (s.fit !== null) patch({ fit: null }); return; }
    patch({ fit: measurer.fit(s.draft.trim(), t) });
  };

  const applyList = (list: AlertListPayload): void => { patch({ pinned: list.pinned, recent: list.recent, listError: null }); };

  async function refreshList(): Promise<void> {
    const token = ++listToken;
    try {
      const list = await api.request("alert.list", {});
      if (!disposed && token === listToken) applyList(list);   // gana siempre la última lectura
    } catch (error) {
      if (!disposed && token === listToken) patch({ listError: error instanceof Error ? error.message : "No se pudo leer la lista de alertas." });
    }
  }

  const scheduleListRefresh = (): void => {
    if (refreshTimer) clearTimeout(refreshTimer);
    refreshTimer = setTimeout(() => { refreshTimer = null; void refreshList(); }, listRefreshDelayMs);
  };

  const offChanged = api.subscribe("alert.changed", () => { if (!disposed) void refreshList(); });

  /** ¿Se puede mostrar este texto de una alerta GUARDADA con el modo elegido? Motivo (y si se ofrece el desplazamiento) si no. Misma regla que el cuadro. */
  const savedProblem = (text: string): { message: string; canTicker: boolean } | null => {
    const s = store.getState();
    const tickerOk = s.settings?.tickerEnabled !== false;
    if (s.mode === "ticker") return tickerOk ? null : { message: "El desplazamiento está desactivado en los ajustes de las alertas: elige la cinta.", canTicker: false };
    const t = theme();
    if (!measurer || !t) return null;
    const fit = measurer.fit(text, t);
    if (fit.state === "fits" || fit.state === "shrunk") return null;
    const why = fit.state === "grows"
      ? `No cabe en ${String(t.maxLines)} renglones ni con la letra mínima (${String(fit.fontSize)} px): la cinta crecería.`
      : "No cabe en la cinta ni creciendo: se cortaría.";
    return { message: `${why} ${tickerOk ? "Puedes mostrarlo con desplazamiento." : "El desplazamiento está desactivado en los ajustes: acorta el aviso."}`, canTicker: tickerOk };
  };

  const sendShow = (text: string, mode: AlertMode): void => {
    api.send("alert.show", { text, mode, durationSec: store.getState().durationSec });
    scheduleListRefresh();
  };

  const controller: AlertController = {
    store,
    async init() {
      try {
        const settings = await api.request("alert.getSettings", {});
        if (disposed) return;
        // El modo y la duración del cuadro parten de los ajustes SOLO la primera vez (no se pisa lo que el operador ya eligió).
        const first = store.getState().settings === null;
        patch(first ? { settings, mode: settings.mode, durationSec: settings.durationSec, settingsError: null } : { settings, settingsError: null });
        refit();
      } catch (error) {
        if (!disposed) patch({ settingsError: error instanceof Error ? error.message : "No se pudieron leer los ajustes de las alertas." });
      }
      await refreshList();
    },
    refreshList,
    setDraft(text) {
      patch({ draft: text.length > ALERT_MAX_CHARS * 2 ? text.slice(0, ALERT_MAX_CHARS * 2) : text, loadedFrom: null });
      refit();
    },
    setMode(mode) {
      patch({ mode, savedNotice: null });
      refit();
    },
    setDuration(sec) { patch({ durationSec: sec }); },
    loadFrom(alert) {
      patch({ draft: alert.text, loadedFrom: alert.id });
      refit();
    },
    showProblem() {
      const s = store.getState();
      const text = s.draft.trim();
      if (text.length === 0) return "Escribe el aviso.";
      if (text.length > ALERT_MAX_CHARS) return `El aviso supera los ${String(ALERT_MAX_CHARS)} caracteres.`;
      if (s.mode === "ticker" && s.settings?.tickerEnabled === false) return "El desplazamiento está desactivado en los ajustes.";
      if (s.mode === "band" && s.fit?.state === "blocked") return "No cabe en la cinta: acórtalo o usa el desplazamiento.";
      return null;
    },
    show() {
      if (controller.showProblem() !== null) return false;
      patch({ savedNotice: null });
      sendShow(store.getState().draft.trim(), store.getState().mode);
      return true;
    },
    showSaved(alert) {
      if (alert.text.length === 0) return false;
      const problem = savedProblem(alert.text);
      if (problem) { patch({ savedNotice: { alertId: alert.id, ...problem } }); return false; }
      patch({ savedNotice: null });
      sendShow(alert.text, store.getState().mode);
      return true;
    },
    showSavedAsTicker(alert) {
      if (alert.text.length === 0 || store.getState().settings?.tickerEnabled === false) return false;
      patch({ mode: "ticker", savedNotice: null });
      sendShow(alert.text, "ticker");
      return true;
    },
    dismissSavedNotice() { patch({ savedNotice: null }); },
    hide() { api.send("alert.hide", {}); },
    pin(alert, pinned) {
      if (pinned && store.getState().pinned.length >= ALERT_MAX_PINNED && !alert.pinned) {
        patch({ listError: `Ya hay ${String(ALERT_MAX_PINNED)} alertas fijadas: desfija o borra alguna antes.` });
        return;
      }
      api.send("alert.save", { id: alert.id, text: alert.text, pinned });
    },
    saveDraftPinned() {
      const s = store.getState();
      const text = s.draft.trim();
      if (text.length === 0 || text.length > ALERT_MAX_CHARS) return;
      if (s.pinned.length >= ALERT_MAX_PINNED) { patch({ listError: `Ya hay ${String(ALERT_MAX_PINNED)} alertas fijadas: desfija o borra alguna antes.` }); return; }
      api.send("alert.save", { id: null, text, pinned: true });
    },
    remove(alert) { api.send("alert.delete", { id: alert.id }); },
    saveSettings(settings) {
      const problem = alertSettingsProblem(settings);
      if (problem) { patch({ settingsError: problem }); return; }
      patch({ settingsError: null });
      api.send("alert.setSettings", settings);
      // El backend es quien manda: se relee lo guardado (y se vuelve a medir con el tema nuevo). Mientras tanto la vista no inventa nada.
      patch({ settings });
      refit();
      void api.request("alert.getSettings", {}).then((confirmed) => { if (!disposed) { patch({ settings: confirmed }); refit(); } }, () => undefined);
    },
    requestFocus() { patch({ focusTick: store.getState().focusTick + 1 }); },
    setShortcut(action, combo) {
      const other = action === "focus" ? "hide" : "focus";
      const problem = validateCombo(combo, [store.getState().shortcuts[other]]);
      if (problem) { patch({ shortcutError: problem }); return problem; }
      const next = { ...store.getState().shortcuts, [action]: combo };
      patch({ shortcuts: next, shortcutError: null });
      saveBindings(shortcutStorage, next);
      return null;
    },
    resetShortcuts() {
      patch({ shortcuts: { ...DEFAULT_ALERT_SHORTCUTS }, shortcutError: null });
      saveBindings(shortcutStorage, DEFAULT_ALERT_SHORTCUTS);
    },
    dispose() {
      disposed = true;
      if (refreshTimer) clearTimeout(refreshTimer);
      offChanged();
    },
  };
  return controller;
}
