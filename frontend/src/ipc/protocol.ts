// Reflejo manual de OakProyektor.Ipc / OakProyektor.Core. Un test de contrato (backend/tests) compara:
// PROTOCOL_VERSION, MESSAGE_CATALOG, MASTER_MODES y los campos de cada interfaz "payload" con los tipos de C#.
// Mantén las interfaces planas (sin objetos anidados en línea) para que ese test pueda leerlas.

/**
 * v2: snapshots con `seq` global creciente (las réplicas descartan seq <= la última); monitores por `id` (no por índice);
 * replies `{ ok, result, error }`; evento `command.failed`; `output.setMaster` y `output.recover` van por un carril de
 * prioridad en el backend (el orden entre carriles no está garantizado; dentro de cada uno sí).
 * v3: `output.recover` lleva `{ reason: "manual" | "emergency" }` (el atajo de respaldo de la salida se distingue del botón del panel).
 * v6: módulo de Biblias (`bible.*`). Los mensajes de importación nunca llevan texto bíblico, solo conteos y referencias.
 * v7: motor multimedia por capas: `OutputState` lleva `schemaVersion`, `background`, `content` y `overlays` (ya no `backgroundColor` ni `text`);
 * comandos `output.setBackground`, `output.showContent`, `output.clearContent` y `output.reportBackgroundFailure` (este lo manda la salida);
 * evento `output.backgroundFailed`; errores `output.mediaRejected` y `output.invalidContent`.
 */
export const PROTOCOL_VERSION = 7;

export type MessageKind = "command" | "event" | "query" | "reply";

export const MESSAGE_CATALOG = [
  { type: "output.open", kind: "command" },
  { type: "output.close", kind: "command" },
  { type: "output.setMaster", kind: "command" },
  { type: "output.recover", kind: "command" },
  { type: "output.setBackground", kind: "command" },
  { type: "output.showContent", kind: "command" },
  { type: "output.clearContent", kind: "command" },
  { type: "output.reportBackgroundFailure", kind: "command" },
  { type: "output.backgroundFailed", kind: "event" },
  { type: "settings.setAutoRestore", kind: "command" },
  { type: "settings.setDisplayLostPolicy", kind: "command" },
  { type: "settings.setPanelAlertOnOutput", kind: "command" },
  { type: "display.list", kind: "query" },
  { type: "display.list", kind: "reply" },
  { type: "output.getState", kind: "query" },
  { type: "output.getState", kind: "reply" },
  { type: "display.changed", kind: "event" },
  { type: "output.stateChanged", kind: "event" },
  { type: "command.failed", kind: "event" },
  { type: "database.getStatus", kind: "query" },
  { type: "database.getStatus", kind: "reply" },
  { type: "database.statusChanged", kind: "event" },
  { type: "bible.import", kind: "command" },
  { type: "bible.setAttribution", kind: "command" },
  { type: "bible.list", kind: "query" },
  { type: "bible.list", kind: "reply" },
  { type: "bible.lookup", kind: "query" },
  { type: "bible.lookup", kind: "reply" },
  { type: "bible.chapter", kind: "query" },
  { type: "bible.chapter", kind: "reply" },
  { type: "bible.search", kind: "query" },
  { type: "bible.search", kind: "reply" },
  { type: "bible.history", kind: "query" },
  { type: "bible.history", kind: "reply" },
  { type: "bible.suggest", kind: "query" },
  { type: "bible.suggest", kind: "reply" },
  { type: "bible.importProgress", kind: "event" },
  { type: "bible.importFinished", kind: "event" },
] as const;

export const MASTER_MODES = ["live", "clearText", "black", "logo", "freeze"] as const;
export type MasterMode = (typeof MASTER_MODES)[number];

export const DATABASE_STATES = ["initializing", "ready", "failed"] as const;
export type DatabaseState = (typeof DATABASE_STATES)[number];

export const PAUSE_REASONS = ["displayLost", "panelCrossed", "manual", "emergency", "duplicateMode", "unstable"] as const;
export type OutputPauseReason = (typeof PAUSE_REASONS)[number];

/** Qué hacer si el monitor de la salida desaparece: avisar y esperar (por defecto) o mover sola al mejor monitor que no sea el del panel. */
export const DISPLAY_LOST_POLICIES = ["warnAndWait", "moveToBest"] as const;
export type DisplayLostPolicy = (typeof DISPLAY_LOST_POLICIES)[number];

export type EmptyPayload = Record<string, never>;

/** `id` es la ÚNICA forma de referirse a un monitor; no uses la posición en la lista. */
export interface DisplayInfo {
  id: string;
  x: number;
  y: number;
  width: number;
  height: number;
  scale: number;
  isPrimary: boolean;
  hostsPanel: boolean;
}

/** Tipo de fondo (capa 1). */
export type BackgroundKind = "none" | "color" | "image" | "video";
export type TextAlign = "left" | "center" | "right";
export type VerticalAlign = "top" | "middle" | "bottom";
/** Por qué la salida no pudo reproducir el fondo (el texto para el operador lo pone el backend). */
export type BackgroundFailureReason = "unsupportedFormat" | "decodeError" | "readError" | "stalled" | "imageError";

/**
 * Capa 1. `color` es siempre un color válido: el fondo de tipo color y el color de reserva si el medio falla. `mediaUrl` es una URL `file:///` ya VALIDADA
 * por el backend. `warning`: `removableDrive` = el archivo está en una unidad extraíble (solo informativo para el panel).
 */
export interface BackgroundLayer {
  kind: BackgroundKind;
  color: string;
  mediaUrl: string | null;
  fileName: string | null;
  muted: boolean;
  transitionMs: number;
  warning: string | null;
}

export interface TextBlock {
  text: string;
}

/** Medidas en píxeles del lienzo virtual de 1920×1080. */
export interface TextTheme {
  fontFamily: string;
  fontSize: number;
  color: string;
  outlineWidth: number;
  outlineColor: string;
  shadowBlur: number;
  shadowOffset: number;
  shadowColor: string;
  align: TextAlign;
  verticalAlign: VerticalAlign;
  lineHeight: number;
  marginTop: number;
  marginRight: number;
  marginBottom: number;
  marginLeft: number;
  autoFit: boolean;
  minFontSize: number;
}

export interface Slide {
  id: string;
  blocks: TextBlock[];
  theme: TextTheme;
}

export interface ContentLayer {
  slide: Slide | null;
  transitionMs: number;
}

export interface OverlayItem {
  id: string;
  kind: string;
}

/** Capa 3: solo la estructura; todavía no hay alertas ni ticker. */
export interface OverlayLayer {
  items: OverlayItem[];
}

export interface OutputState {
  schemaVersion: number;
  revision: number;
  master: MasterMode;
  background: BackgroundLayer;
  content: ContentLayer;
  overlays: OverlayLayer;
}

export interface OutputSetBackgroundPayload {
  kind: BackgroundKind;
  path: string | null;
  color: string | null;
  muted: boolean;
  transitionMs: number;
}

export interface OutputShowContentPayload {
  blocks: string[];
  theme: TextTheme;
  transitionMs: number;
}

export interface OutputClearContentPayload {
  transitionMs: number;
}

export interface OutputReportBackgroundFailurePayload {
  mediaUrl: string;
  reason: BackgroundFailureReason;
  detail: string | null;
}

export interface OutputBackgroundFailedPayload {
  reason: BackgroundFailureReason;
  message: string;
  fileName: string | null;
}

export interface OutputOpenPayload {
  displayId: string | null;
}

export interface OutputSetMasterPayload {
  master: MasterMode;
}

export interface DisplayListPayload {
  seq: number;
  displays: DisplayInfo[];
}

/** Quién pide la pausa: el botón «Recuperar control» del panel (`"manual"`) o el atajo de emergencia de la página de salida (`"emergency"`). */
export type RecoverReason = "manual" | "emergency";

export interface OutputRecoverPayload {
  reason: RecoverReason;
}

export interface SetAutoRestorePayload {
  enabled: boolean;
}

export interface SetDisplayLostPolicyPayload {
  policy: DisplayLostPolicy;
}

export interface SetPanelAlertOnOutputPayload {
  enabled: boolean;
}

/** Un atajo global de respaldo (independiente del panel) y si Windows permitió registrarlo. */
export interface HotkeyStatus {
  action: string;
  keys: string;
  registered: boolean;
}

export interface OutputWindowStatus {
  isOpen: boolean;
  displayId: string | null;
  isWindowed: boolean;
  isPaused: boolean;
  pauseReason: OutputPauseReason | null;
  autoRestore: boolean;
  emergencyHotkey: string | null;
  /** Monitor preferido del operador, al que la salida siempre intenta volver. */
  preferredDisplayId: string | null;
  onDisplayLost: DisplayLostPolicy;
  /** El JavaScript del panel lleva ~10 s sin dar señales. La salida sigue proyectando. */
  panelUnresponsive: boolean;
  /** Ajuste del operador: la salida muestra un aviso cuando panelUnresponsive (por defecto NO). */
  panelAlertOnOutput: boolean;
  hotkeys: HotkeyStatus[];
}

export interface OutputStateChangedPayload {
  seq: number;
  state: OutputState;
  window: OutputWindowStatus;
}

export interface IpcErrorPayload {
  code: string;
  message: string;
}

/** Forma común de todo reply. */
export interface IpcReplyFields {
  ok: boolean;
  result: unknown;
  error: IpcErrorPayload | null;
}

/** Payload de un reply, discriminado por `ok`. */
export type ReplyPayload<T> = { ok: true; result: T; error: null } | { ok: false; result: null; error: IpcErrorPayload };

/** Si `state` es `failed`, `message` explica qué pasó y `backupPath` (si lo hay) es la copia previa a la migración, con los datos originales. */
export interface DatabaseStatusPayload {
  seq: number;
  state: DatabaseState;
  message: string | null;
  backupPath: string | null;
}

export interface CommandFailedPayload {
  commandId: string;
  commandType: string;
  error: IpcErrorPayload;
}

// ───────────── Biblias (v6) ─────────────

/** Una Biblia instalada. `attribution` es su aviso de derechos (vacío si no tiene); `numberingProfile`, el perfil de numeración con que se importó (null = ninguno). */
export interface BibleDto {
  id: number;
  abbreviation: string;
  name: string;
  language: string;
  attribution: string;
  numberingProfile: string | null;
  books: number;
  verses: number;
}

export interface BibleListPayload {
  bibles: BibleDto[];
}

/** `reference` es lo que escribió el operador («Jn 3:16»); `bible` null = la predeterminada; `record` = anotarla en el historial. */
export interface BibleLookupRequest {
  reference: string;
  bible: string | null;
  record: boolean;
}

/** Un capítulo completo por número de libro canónico (1 a 66). */
export interface BibleChapterRequest {
  bible: string | null;
  bookNumber: number;
  chapter: number;
}

export interface BibleVerseDto {
  bookNumber: number;
  bookName: string;
  chapter: number;
  verseNumber: number;
  text: string;
}

/** Reply de `bible.lookup` y `bible.chapter`. `attribution` es el aviso de derechos de la Biblia. */
export interface BiblePassagePayload {
  bible: string;
  attribution: string;
  reference: string;
  verses: BibleVerseDto[];
}

/** `limit` <= 0 = el predeterminado (50); máximo 200. Palabras sueltas = todas; entre comillas = frase. */
export interface BibleSearchRequest {
  query: string;
  bible: string | null;
  limit: number;
}

/** `fragment` lleva las coincidencias entre « y ». */
export interface BibleSearchHitDto {
  bible: string;
  bookNumber: number;
  bookName: string;
  chapter: number;
  verseNumber: number;
  fragment: string;
}

export interface BibleSearchPayload {
  query: string;
  truncated: boolean;
  hits: BibleSearchHitDto[];
}

/** `limit` <= 0 = todo el historial guardado. */
export interface BibleHistoryRequest {
  limit: number;
}

export interface CitationDto {
  id: number;
  bible: string;
  bookNumber: number;
  bookName: string;
  chapter: number;
  verseStart: number | null;
  verseEnd: number | null;
  label: string;
  citedAt: string;
}

export interface BibleHistoryPayload {
  entries: CitationDto[];
}

export interface BibleSuggestRequest {
  prefix: string;
  limit: number;
}

export interface BibleSuggestionDto {
  bookNumber: number;
  name: string;
  shortName: string;
  matchedAlias: string;
}

export interface BibleSuggestPayload {
  suggestions: BibleSuggestionDto[];
}

/** Edita el aviso de derechos de una Biblia (vacío = quitarlo). */
export interface BibleSetAttributionPayload {
  bible: string;
  attribution: string;
}

/** Importa en segundo plano: el comando vuelve al instante; avance en `bible.importProgress`, resultado en `bible.importFinished`. */
export interface BibleImportPayload {
  path: string;
  abbreviation: string;
  name: string;
  language: string;
  numberingProfile: string | null;
  attribution: string | null;
}

/** `phase`: reading | cleaning | saving | validating. */
export interface BibleImportProgressPayload {
  abbreviation: string;
  phase: string;
  done: number;
  total: number;
}

/** `kind` = `playlist` (elemento de un servicio) o `history`: apuntaba a un versículo que la reimportación eliminó. */
export interface AffectedItemDto {
  kind: string;
  id: number;
  playlistId: number | null;
  reference: string;
}

/** Solo conteos y referencias: nunca texto bíblico. */
export interface BibleImportReportDto {
  bibleId: number;
  created: boolean;
  numberingProfile: string;
  sourceVerses: number;
  inserted: number;
  updated: number;
  unchanged: number;
  deleted: number;
  cleanedVerses: number;
  joinedVerses: number;
  books: number;
  chapters: number;
  verses: number;
  emptyVerses: number;
  verseTotalDifference: number;
  ftsIntegrityOk: boolean;
  affectedItems: AffectedItemDto[];
  warnings: string[];
  elapsedMs: number;
}

export interface BibleImportFinishedPayload {
  abbreviation: string;
  ok: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  report: BibleImportReportDto | null;
}

/** Payload de cada comando (UI → C#). */
export interface CommandPayloads {
  "output.open": OutputOpenPayload;
  "output.close": EmptyPayload;
  "output.setMaster": OutputSetMasterPayload;
  "output.recover": OutputRecoverPayload;
  "output.setBackground": OutputSetBackgroundPayload;
  "output.showContent": OutputShowContentPayload;
  "output.clearContent": OutputClearContentPayload;
  "output.reportBackgroundFailure": OutputReportBackgroundFailurePayload;
  "settings.setAutoRestore": SetAutoRestorePayload;
  "settings.setDisplayLostPolicy": SetDisplayLostPolicyPayload;
  "settings.setPanelAlertOnOutput": SetPanelAlertOnOutputPayload;
  "bible.import": BibleImportPayload;
  "bible.setAttribution": BibleSetAttributionPayload;
}

/** Payload de cada evento (C# → UI). */
export interface EventPayloads {
  "display.changed": DisplayListPayload;
  "output.stateChanged": OutputStateChangedPayload;
  "command.failed": CommandFailedPayload;
  "output.backgroundFailed": OutputBackgroundFailedPayload;
  "database.statusChanged": DatabaseStatusPayload;
  "bible.importProgress": BibleImportProgressPayload;
  "bible.importFinished": BibleImportFinishedPayload;
}

/** Payload de cada query y de su reply. */
export interface QueryPayloads {
  "display.list": { request: EmptyPayload; reply: DisplayListPayload };
  "output.getState": { request: EmptyPayload; reply: OutputStateChangedPayload };
  "database.getStatus": { request: EmptyPayload; reply: DatabaseStatusPayload };
  "bible.list": { request: EmptyPayload; reply: BibleListPayload };
  "bible.lookup": { request: BibleLookupRequest; reply: BiblePassagePayload };
  "bible.chapter": { request: BibleChapterRequest; reply: BiblePassagePayload };
  "bible.search": { request: BibleSearchRequest; reply: BibleSearchPayload };
  "bible.history": { request: BibleHistoryRequest; reply: BibleHistoryPayload };
  "bible.suggest": { request: BibleSuggestRequest; reply: BibleSuggestPayload };
}

export type CommandType = keyof CommandPayloads;
export type EventType = keyof EventPayloads;
export type QueryType = keyof QueryPayloads;

interface Envelope<K extends MessageKind, T extends string, P> {
  v: typeof PROTOCOL_VERSION;
  id: string;
  kind: K;
  type: T;
  payload: P;
}

/** Uniones discriminadas por `kind` y `type`. */
export type CommandMessage = { [T in CommandType]: Envelope<"command", T, CommandPayloads[T]> }[CommandType];
export type EventMessage = { [T in EventType]: Envelope<"event", T, EventPayloads[T]> }[EventType];
export type QueryMessage = { [T in QueryType]: Envelope<"query", T, QueryPayloads[T]["request"]> }[QueryType];
export type ReplyMessage = { [T in QueryType]: Envelope<"reply", T, ReplyPayload<QueryPayloads[T]["reply"]>> }[QueryType];
export type IncomingMessage = EventMessage | ReplyMessage;




