import { createStore, type StoreApi } from "zustand/vanilla";
import type { BibleDto, BibleImportFinishedPayload, BibleImportPayload, CommandFailedPayload } from "@/ipc/protocol";
import { cleanPath } from "../themes";

/** Lo mínimo del cliente IPC que usa la importación (así se prueba con uno falso). */
export interface ImportApi {
  send(type: "bible.import", payload: BibleImportPayload): void;
  subscribe(type: "bible.importProgress", handler: (payload: { abbreviation: string; phase: string; done: number; total: number }) => void): () => void;
  subscribe(type: "bible.importFinished", handler: (payload: BibleImportFinishedPayload) => void): () => void;
  subscribe(type: "command.failed", handler: (payload: CommandFailedPayload) => void): () => void;
}

/** Perfiles de numeración que conoce el backend (`NumberingProfiles` en Core; un test de C# comprueba que coinciden). */
export const NUMBERING_PROFILES: readonly { id: string; label: string }[] = [
  { id: "none", label: "Ninguno: respetar la numeración del archivo" },
  { id: "merge-ps47-3jn", label: "Unir Salmos 47:9-10 y 3 Juan 14-15 (31 102 versículos, como la RVR1960 impresa)" },
];

export const MAX_ABBREVIATION_LENGTH = 20;

/**
 * Qué indicador explícito permite saltarse cada rechazo de una reimportación (protecciones del backend): `deletion` = `allowLargeDeletion` (versículos eliminados en masa o un libro o
 * capítulo entero que desaparece), `change` = `allowLargeChange` (muchos textos cambiados, el nombre de la Biblia o textos que quedarían vacíos: otra traducción con la misma abreviatura).
 */
export type AllowKind = "deletion" | "change";

const ALLOW_BY_CODE: Record<string, AllowKind> = {
  "bible.tooManyDeletions": "deletion",
  "bible.booksWouldDisappear": "deletion",
  "bible.tooManyChanges": "change",
};

/** El indicador que permitiría repetir la importación rechazada con este código, o null si el rechazo no se puede saltar (archivo ilegible, ruta inexistente…). */
export function allowKindFor(errorCode: string | null | undefined): AllowKind | null {
  return (errorCode && ALLOW_BY_CODE[errorCode]) || null;
}

export interface ImportForm {
  path: string;
  abbreviation: string;
  name: string;
  language: string;
  /** Id del perfil (`none` = ninguno). */
  numberingProfile: string;
  /** Aviso de derechos. Vacío = no tocar el actual (Biblia ya instalada) o ninguno (nueva). */
  attribution: string;
  /** El operador confirmó que quiere ACTUALIZAR una Biblia ya instalada con esa abreviatura. */
  confirmUpdate: boolean;
}

export const EMPTY_FORM: ImportForm = { path: "", abbreviation: "", name: "", language: "es", numberingProfile: "none", attribution: "", confirmUpdate: false };

export interface ImportCheck {
  /** Motivos por los que NO se puede importar todavía (vacío = se puede). */
  errors: string[];
  /** Avisos que no bloquean. */
  warnings: string[];
  /** La Biblia ya instalada con exactamente esa abreviatura: importar la ACTUALIZA en el sitio. */
  existing: BibleDto | undefined;
}

/**
 * Comprobación del formulario ANTES de mandar nada. Protecciones (revisión del módulo de Biblias): una abreviatura ya instalada exige confirmación explícita
 * (reimportar con una abreviatura existente sobrescribe TODO su texto y el tope del 1 % solo cuenta versículos borrados, no cambiados); una abreviatura que solo
 * difiere en mayúsculas se rechaza (crearía una Biblia «gemela»). La ruta debe ser absoluta (el backend no la resuelve contra ninguna carpeta).
 */
export function checkImportForm(form: ImportForm, installed: readonly BibleDto[]): ImportCheck {
  const errors: string[] = [];
  const warnings: string[] = [];
  const abbreviation = form.abbreviation.trim();
  const path = cleanPath(form.path);

  if (path.length === 0) errors.push("Indica la ruta del archivo.");
  else if (!/^[A-Za-z]:[\\/]/.test(path)) errors.push("La ruta debe ser completa, con letra de unidad (por ejemplo C:\\Biblias\\version.json).");
  else if (!/\.json$/i.test(path)) warnings.push("El archivo no termina en .json: solo se admite el formato JSON por capítulos (youversion-json).");

  if (abbreviation.length === 0) errors.push("Indica la abreviatura (por ejemplo NVI).");
  else if (abbreviation.length > MAX_ABBREVIATION_LENGTH) errors.push(`La abreviatura admite como máximo ${String(MAX_ABBREVIATION_LENGTH)} caracteres.`);
  if (form.name.trim().length === 0) errors.push("Indica el nombre de la Biblia.");
  if (form.language.trim().length === 0) errors.push("Indica el idioma (por ejemplo es).");
  if (!NUMBERING_PROFILES.some((p) => p.id === form.numberingProfile)) errors.push("Perfil de numeración desconocido.");

  // La abreviatura se compara SIN distinguir mayúsculas (como el backend): «rvr1960» y «RVR1960» son la MISMA Biblia, y se actualiza la guardada.
  const existing = installed.find((b) => b.abbreviation === abbreviation) ?? installed.find((b) => b.abbreviation.toLowerCase() === abbreviation.toLowerCase());
  if (existing && !form.confirmUpdate) errors.push(`Ya hay una Biblia «${existing.abbreviation}»: marca la confirmación para ACTUALIZARLA (se sobrescribe su texto).`);
  if (existing && form.numberingProfile !== (existing.numberingProfile ?? "none"))
    warnings.push(`El perfil de numeración cambia de «${existing.numberingProfile ?? "none"}» a «${form.numberingProfile}»: se cambiarán versículos unidos o partidos y los servicios que apunten a ellos pueden verse afectados.`);
  return { errors, warnings, existing };
}

export type ImportStatus = "idle" | "running" | "finished";

export interface ImportViewState {
  status: ImportStatus;
  abbreviation: string | null;
  phase: string | null;
  done: number;
  total: number;
  result: BibleImportFinishedPayload | null;
  /** Hay una importación anterior que se puede repetir permitiendo lo que se rechazó (`retryAllowing`). */
  canRetryAllowing: boolean;
}

const initial: ImportViewState = { status: "idle", abbreviation: null, phase: null, done: 0, total: 0, result: null, canRetryAllowing: false };

export interface ImportController {
  store: StoreApi<ImportViewState>;
  /** Manda la importación. Devuelve false si no se pudo pedir (ya hay una en curso en esta vista). */
  start: (form: ImportForm) => boolean;
  /**
   * Repite la ÚLTIMA importación permitiendo lo que el backend rechazó (el indicador que corresponde al código del rechazo). Solo tiene efecto si la última terminó con un rechazo que
   * se puede saltar; la interfaz exige antes una confirmación explícita del operador.
   */
  retryAllowing: () => boolean;
  /** Cierra el informe (o deja de mostrar el avance): una importación en curso sigue en el backend. */
  dismiss: () => void;
  dispose: () => void;
}

/**
 * Importación de Biblias desde el panel. Sin estado de negocio: manda `bible.import` (el backend importa en un hilo de fondo, UNA a la vez) y refleja los eventos de
 * avance y resultado. Vive con la página, así que cambiar de pantalla no pierde el avance. `onFinished` avisa al terminar (para refrescar la lista de Biblias).
 */
export function createImportController(api: ImportApi, onFinished?: (result: BibleImportFinishedPayload) => void): ImportController {
  const store = createStore<ImportViewState>(() => ({ ...initial }));
  let lastForm: ImportForm | null = null;
  const send = (form: ImportForm, allow: { deletion: boolean; change: boolean }): boolean => {
    if (store.getState().status === "running") return false;
    lastForm = form;
    const abbreviation = form.abbreviation.trim();
    store.setState({ status: "running", abbreviation, phase: "reading", done: 0, total: 1, result: null, canRetryAllowing: false });
    api.send("bible.import", {
      path: cleanPath(form.path),
      abbreviation,
      name: form.name.trim(),
      language: form.language.trim(),
      // SIEMPRE explícito (incluido «none»): un perfil nulo significaría «no indicado» y el backend conservaría el guardado.
      numberingProfile: form.numberingProfile,
      attribution: form.attribution.trim().length === 0 ? null : form.attribution.trim(),
      allowLargeDeletion: allow.deletion,
      allowLargeChange: allow.change,
    });
    return true;
  };

  const offs = [
    api.subscribe("bible.importProgress", (p) => {
      if (store.getState().status !== "running") return;
      store.setState({ phase: p.phase, done: p.done, total: p.total });
    }),
    api.subscribe("bible.importFinished", (result) => {
      store.setState({ status: "finished", abbreviation: result.abbreviation, result, phase: null, canRetryAllowing: !result.ok && lastForm !== null && allowKindFor(result.errorCode) !== null });
      onFinished?.(result);
    }),
    // El backend rechazó el comando sin llegar a importar (p. ej. «bible.importBusy»: ya hay otra importación): se muestra como una importación fallida.
    api.subscribe("command.failed", (failure) => {
      if (failure.commandType !== "bible.import") return;
      store.setState({
        status: "finished",
        phase: null,
        result: { abbreviation: store.getState().abbreviation ?? "", ok: false, errorCode: failure.error.code, errorMessage: failure.error.message, report: null },
        canRetryAllowing: false,
      });
    }),
  ];

  return {
    store,
    start(form) {
      return send(form, { deletion: false, change: false });
    },
    retryAllowing() {
      const result = store.getState().result;
      const kind = result && !result.ok ? allowKindFor(result.errorCode) : null;
      if (!lastForm || kind === null) return false;
      return send(lastForm, { deletion: kind === "deletion", change: kind === "change" });
    },    dismiss() {
      store.setState({ ...initial });
    },
    dispose() {
      offs.forEach((off) => {
        off();
      });
    },
  };
}
