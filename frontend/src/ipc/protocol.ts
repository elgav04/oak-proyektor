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
 * v8: configuración de la proyección de Biblia: query `bible.getSettings` y comando `bible.setSettings` (en settings.json); error `bible.invalidSettings`.
 * v11: `bible.import` lleva `allowLargeDeletion` y `allowLargeChange` (protecciones de la reimportación); su informe lleva `previousVerses`, `backupPath`, `numberingProfileKept`,
 * `discardedVerses` y `duplicateChaptersDifferent`; un `numberingProfile` nulo CONSERVA el perfil guardado.
 * v10: query `settings.getStatus` (avisos de la carga de settings.json: bloques inválidos vueltos a su valor por defecto, archivo dañado).
 * v9: `BackgroundLayer.retry` (contador de reintentos del mismo medio) y comando `output.retryBackground` («Reintentar» el fondo que falló).
 * v12: módulo de canciones (`song.*`): queries `song.search`, `song.list`, `song.get`, `song.parseText`, `song.history` y `song.getSettings`; comandos `song.setSettings`, `song.save`,
 * `song.restoreRevision` y `song.delete`; evento `song.changed` (resultado de guardar, restaurar o borrar). La letra viaja solo en las consultas y comandos que la necesitan.
 * v13: listas de culto (`playlist.*`): queries `playlist.list`, `playlist.get` y `playlist.getSettings`; comandos `playlist.setSettings`, `playlist.create`, `playlist.update`, `playlist.addItem`,
 * `playlist.moveItem`, `playlist.removeItem`, `playlist.updateItem`, `playlist.duplicate` y `playlist.delete`; evento `playlist.changed`. `output.showContent` lleva `origin` (ids de lista y
 * elemento, o nulo), el `Slide` lo conserva y `OutputState.schemaVersion` pasa a 2. `song.get` y `song.changed` llevan `usedInLists` (las listas por id, nombre y fecha).
 * v14: `playlist.delete` lleva `expectedRevision` (con una revisión caducada falla con `playlist.conflict` y no borra nada).
 * v17: biblioteca local de medios (docs/DISENO_MEDIOS.md): queries `media.list` y `media.get`; comandos `media.import`, `media.cancelImport`, `media.rename`, `media.move`, `media.delete`,
 * `media.createFolder`, `media.renameFolder`, `media.deleteFolder`, `media.reportProbe`, `media.setThumbnail`, `media.verifyLibrary` y `media.setBackground`; eventos `media.importProgress`,
 * `media.importFinished` y `media.changed` (sin nombres ni rutas en eventos de progreso; ids y bytes).
 * v16: `AlertSettingsPayload` lleva `tickerEnabled` (interruptor de seguridad del desplazamiento; apagado, `alert.show` en modo `ticker` se rechaza).
 * v15: alertas rápidas (capa 3): `OutputState.overlays` pasa de `{ items }` a `{ alert }` (`schemaVersion` 3); comandos `alert.show` y `alert.hide` (carril de PRIORIDAD: solo tocan memoria),
 * `alert.setSettings`, `alert.save` y `alert.delete`; queries `alert.getSettings` y `alert.list`; evento `alert.changed` (solo ids). Freeze ya NO congela la alerta.
 */
export const PROTOCOL_VERSION = 17;

export type MessageKind = "command" | "event" | "query" | "reply";

export const MESSAGE_CATALOG = [
  { type: "output.open", kind: "command" },
  { type: "output.close", kind: "command" },
  { type: "output.setMaster", kind: "command" },
  { type: "output.recover", kind: "command" },
  { type: "output.setBackground", kind: "command" },
  { type: "output.showContent", kind: "command" },
  { type: "output.clearContent", kind: "command" },
  { type: "output.retryBackground", kind: "command" },
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
  { type: "settings.getStatus", kind: "query" },
  { type: "settings.getStatus", kind: "reply" },
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
  { type: "bible.getSettings", kind: "query" },
  { type: "bible.getSettings", kind: "reply" },
  { type: "song.search", kind: "query" },
  { type: "song.search", kind: "reply" },
  { type: "song.list", kind: "query" },
  { type: "song.list", kind: "reply" },
  { type: "song.get", kind: "query" },
  { type: "song.get", kind: "reply" },
  { type: "song.parseText", kind: "query" },
  { type: "song.parseText", kind: "reply" },
  { type: "song.history", kind: "query" },
  { type: "song.history", kind: "reply" },
  { type: "song.getSettings", kind: "query" },
  { type: "song.getSettings", kind: "reply" },
  { type: "song.setSettings", kind: "command" },
  { type: "song.save", kind: "command" },
  { type: "song.restoreRevision", kind: "command" },
  { type: "song.delete", kind: "command" },
  { type: "song.changed", kind: "event" },
  { type: "playlist.list", kind: "query" },
  { type: "playlist.list", kind: "reply" },
  { type: "playlist.get", kind: "query" },
  { type: "playlist.get", kind: "reply" },
  { type: "playlist.getSettings", kind: "query" },
  { type: "playlist.getSettings", kind: "reply" },
  { type: "playlist.setSettings", kind: "command" },
  { type: "playlist.create", kind: "command" },
  { type: "playlist.update", kind: "command" },
  { type: "playlist.addItem", kind: "command" },
  { type: "playlist.moveItem", kind: "command" },
  { type: "playlist.removeItem", kind: "command" },
  { type: "playlist.updateItem", kind: "command" },
  { type: "playlist.duplicate", kind: "command" },
  { type: "playlist.delete", kind: "command" },
  { type: "playlist.changed", kind: "event" },
  { type: "alert.show", kind: "command" },
  { type: "alert.hide", kind: "command" },
  { type: "alert.getSettings", kind: "query" },
  { type: "alert.getSettings", kind: "reply" },
  { type: "alert.setSettings", kind: "command" },
  { type: "alert.list", kind: "query" },
  { type: "alert.list", kind: "reply" },
  { type: "alert.save", kind: "command" },
  { type: "alert.delete", kind: "command" },
  { type: "alert.changed", kind: "event" },
  { type: "media.list", kind: "query" },
  { type: "media.list", kind: "reply" },
  { type: "media.get", kind: "query" },
  { type: "media.get", kind: "reply" },
  { type: "media.import", kind: "command" },
  { type: "media.cancelImport", kind: "command" },
  { type: "media.rename", kind: "command" },
  { type: "media.move", kind: "command" },
  { type: "media.delete", kind: "command" },
  { type: "media.createFolder", kind: "command" },
  { type: "media.renameFolder", kind: "command" },
  { type: "media.deleteFolder", kind: "command" },
  { type: "media.reportProbe", kind: "command" },
  { type: "media.setThumbnail", kind: "command" },
  { type: "media.verifyLibrary", kind: "command" },
  { type: "media.setBackground", kind: "command" },
  { type: "media.importProgress", kind: "event" },
  { type: "media.importFinished", kind: "event" },
  { type: "media.changed", kind: "event" },
  { type: "bible.setSettings", kind: "command" },
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
  /** Contador de reintentos del MISMO medio: si sube y ese medio había fallado, la salida lo intenta de nuevo (v9). */
  retry: number;
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

/** De dónde viene un slide proyectado desde una lista de culto: SOLO ids y números, nunca texto. `itemType`: "song" | "passage"; `position` = diapositiva o página dentro del elemento (desde 0). */
export interface SlideOrigin {
  playlistId: number;
  itemId: number;
  itemType: string;
  position: number;
}

export interface Slide {
  id: string;
  blocks: TextBlock[];
  theme: TextTheme;
  /** Nulo si la proyección no viene de una lista (Biblia o Canciones sueltas). */
  origin: SlideOrigin | null;
}

export interface ContentLayer {
  slide: Slide | null;
  transitionMs: number;
}

/** Cómo se muestra una alerta: cinta estática (por defecto) o con desplazamiento (para textos largos). */
export type AlertMode = "band" | "ticker";
export type AlertPosition = "bottom" | "top";

/** Tema de la alerta (medidas en píxeles del lienzo de 1920×1080), independiente del de la Biblia y las canciones. */
export interface AlertTheme {
  fontFamily: string;
  fontSize: number;
  color: string;
  outlineWidth: number;
  outlineColor: string;
  /** Fondo de la cinta, `#RRGGBB[AA]`. */
  bandColor: string;
  align: TextAlign;
  position: AlertPosition;
  paddingY: number;
  /** Renglones que admite la cinta estática antes de reducir la letra (1 a 3). */
  maxLines: number;
  autoFit: boolean;
  minFontSize: number;
  /** Velocidad del desplazamiento en píxeles del lienzo por segundo (solo `ticker`). */
  tickerSpeed: number;
}

/**
 * La alerta de la capa 3. `id` identifica la alerta: la salida solo anima al aparecer, desaparecer o cambiar de id. `sticky` = «hasta quitarla» (con tope de 30 min).
 * `expiresAt` (ISO-8601 UTC) es SOLO informativo para la cuenta atrás del panel: quien la quita es el reloj del backend; la salida lo ignora.
 */
export interface AlertOverlay {
  id: string;
  text: string;
  mode: AlertMode;
  theme: AlertTheme;
  sticky: boolean;
  expiresAt: string;
}

/** Capa 3: la alerta, o nulo si no hay ninguna. */
export interface OverlayLayer {
  alert: AlertOverlay | null;
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
  /** Solo ids (lista y elemento); omitido o nulo = no viene de una lista. */
  origin?: SlideOrigin | null;
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

/** Avisos de la carga de settings.json (vacío = todo estaba bien). */
export interface SettingsStatusPayload {
  warnings: string[];
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
  /** null = NO indicado: se conserva el perfil guardado de la Biblia (ninguno si es nueva); «none» = sin uniones, pedido de forma explícita. */
  numberingProfile: string | null;
  attribution: string | null;
  /** Permite eliminar muchos versículos o que desaparezca un libro o capítulo entero (`bible.tooManyDeletions`, `bible.booksWouldDisappear`). */
  allowLargeDeletion: boolean;
  /** Permite cambiar muchos textos, el nombre de la Biblia o dejar textos vacíos (`bible.tooManyChanges`: otra traducción con la misma abreviatura). */
  allowLargeChange: boolean;
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
  /** Versículos que tenía la Biblia antes de importar (0 si es nueva). */
  previousVerses: number;
  /** Copia (`VACUUM INTO`) hecha antes de modificar o borrar algo; null si no hizo falta. */
  backupPath: string | null;
  /** No se indicó perfil de numeración y se conservó el guardado. */
  numberingProfileKept: boolean;
  /** Versículos descartados por numeración inválida o fuera del canon. */
  discardedVerses: number;
  /** Capítulos repetidos en el archivo con texto distinto del primero. */
  duplicateChaptersDifferent: number;
}

export interface BibleImportFinishedPayload {
  abbreviation: string;
  ok: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  report: BibleImportReportDto | null;
}

// ───────────── Configuración de la proyección de Biblia (v8) ─────────────

export interface BibleShortNameDto {
  bible: string;
  shortName: string;
}

/** `defaultBible` null = la primera instalada. El aviso de derechos NO va aquí: es de cada Biblia (`bible.list` / `bible.setAttribution`). */
export interface BibleSettingsPayload {
  defaultBible: string | null;
  projectOnNavigate: boolean;
  superscriptNumbers: boolean;
  showAttribution: boolean;
  transitionMs: number;
  theme: TextTheme;
  shortNames: BibleShortNameDto[];
}

/** Sustituye TODA la configuración (se manda el conjunto completo). */
export interface BibleSetSettingsPayload {
  defaultBible: string | null;
  projectOnNavigate: boolean;
  superscriptNumbers: boolean;
  showAttribution: boolean;
  transitionMs: number;
  theme: TextTheme;
  shortNames: BibleShortNameDto[];
}

// ───────────── Canciones (v12) ─────────────

/** Qué pasa al pulsar «siguiente» en la última diapositiva de una canción. */
export type SongAtEnd = "stay" | "clear";

/**
 * Una sección. `id` null = nueva (al guardar) o sin guardar aún (resultado de `song.parseText`). `kind` se deriva de la etiqueta: al guardar se ignora lo que mande el panel.
 * `text` lleva una línea en blanco por cada corte manual de diapositiva.
 */
export interface SongSectionDto {
  id: number | null;
  kind: string;
  label: string;
  text: string;
}

/** Fondo propio de una canción: `kind` = "image" | "video" | "color". Sin fondo (null) = no se toca el fondo actual de la salida. */
export interface SongBackgroundDto {
  kind: string;
  path: string | null;
  color: string | null;
  muted: boolean;
}

/** Una canción de la lista o de una búsqueda; `fragment` (solo en `song.search`) lleva las coincidencias entre « y ». */
export interface SongSummaryDto {
  id: number;
  title: string;
  author: string | null;
  updatedAt: string;
  fragment: string | null;
}

/** `limit` <= 0 = 50; máximo 200. */
export interface SongSearchRequest {
  query: string;
  limit: number;
}

export interface SongSearchPayload {
  query: string;
  truncated: boolean;
  songs: SongSummaryDto[];
}

/** Todas las canciones por título (sin letra), como mucho 2000. */
export interface SongListPayload {
  truncated: boolean;
  songs: SongSummaryDto[];
}

export interface SongGetRequest {
  id: number;
}

/** `arrangement` son índices (posiciones) en `sections`; `text` es el texto único con etiquetas que reconstruye el backend. */
export interface SongPayload {
  id: number;
  title: string;
  author: string | null;
  copyright: string | null;
  ccliNumber: string | null;
  notes: string | null;
  revision: number;
  createdAt: string;
  updatedAt: string;
  background: SongBackgroundDto | null;
  sections: SongSectionDto[];
  arrangement: number[];
  text: string;
  usedInServices: number;
  usedInLists: PlaylistRefDto[];
}

export interface SongParseTextRequest {
  text: string;
  mergeIdenticalBlocks: boolean;
}

/** `detected`: "empty" | "brackets" | "aliasLines" | "blocks". `errors` impiden guardar; `warnings` solo informan. */
export interface SongParseTextPayload {
  sections: SongSectionDto[];
  arrangement: number[];
  warnings: string[];
  errors: string[];
  detected: string;
}

/** Crea (`id` null) o actualiza una canción COMPLETA; al actualizar `expectedRevision` es obligatorio. `reason`: "save" (por defecto) | "import". */
export interface SongSavePayload {
  id: number | null;
  expectedRevision: number | null;
  title: string;
  author: string | null;
  copyright: string | null;
  ccliNumber: string | null;
  notes: string | null;
  background: SongBackgroundDto | null;
  sections: SongSectionDto[];
  arrangement: number[];
  reason: string | null;
}

export interface SongHistoryRequest {
  songId: number;
}

export interface SongRevisionDto {
  id: number;
  createdAt: string;
  reason: string;
  sectionCount: number;
}

export interface SongHistoryPayload {
  revisions: SongRevisionDto[];
}

/** `revisionId` null = la copia más reciente («Deshacer último guardado»). */
export interface SongRestoreRevisionPayload {
  songId: number;
  revisionId: number | null;
  expectedRevision: number;
}

export interface SongDeletePayload {
  id: number;
}

/** `kind`: "created" | "updated" | "restored" | "deleted". `commandId` = el id del comando que lo causó. Sin letra. */
export interface SongChangedPayload {
  commandId: string;
  songId: number;
  revision: number;
  kind: string;
  usedInServices: number;
  /** Las listas donde estaba la canción (id, nombre y fecha): el aviso de borrado las nombra. */
  usedInLists: PlaylistRefDto[];
}

/** Reply de `song.getSettings` y cuerpo de `song.setSettings`. */
export interface SongSettingsPayload {
  theme: TextTheme;
  transitionMs: number;
  atEnd: SongAtEnd;
}

// ───────────── Listas de culto (v13) ─────────────

export type PlaylistOnItemEnd = "stay" | "next" | "clear";

/** Una lista que incluye una canción. */
export interface PlaylistRefDto {
  id: number;
  name: string;
  serviceDate: string | null;
}

/** Una lista en el listado; `serviceDate` nulo = plantilla. */
export interface PlaylistSummaryDto {
  id: number;
  name: string;
  serviceDate: string | null;
  itemCount: number;
  missingCount: number;
  revision: number;
}

export interface PlaylistListPayload {
  truncated: boolean;
  playlists: PlaylistSummaryDto[];
}

export interface PlaylistGetRequest {
  id: number;
}

/**
 * Un elemento. `itemType`: "song" | "passage" | "note". `title` es el título ACTUAL de la canción. `resolution` (solo pasajes): "resolved" | "bibleNotInstalled" | "passageNotFound";
 * `offeredBible`: la Biblia por defecto que sí tiene el pasaje (se OFRECE, nunca se aplica sola). `projectable` lo calcula el backend: siguiente/anterior saltan lo que no lo es.
 */
export interface PlaylistItemDto {
  id: number;
  position: number;
  itemType: string;
  title: string;
  isMissing: boolean;
  songId: number | null;
  bookNumber: number | null;
  chapter: number | null;
  verseStart: number | null;
  verseEnd: number | null;
  bibleAbbreviation: string | null;
  note: string | null;
  clearOnActivate: boolean;
  resolution: string | null;
  offeredBible: string | null;
  projectable: boolean;
}

export interface PlaylistPayload {
  id: number;
  name: string;
  serviceDate: string | null;
  notes: string;
  revision: number;
  createdAt: string;
  updatedAt: string;
  missingCount: number;
  items: PlaylistItemDto[];
}

export interface PlaylistCreatePayload {
  name: string;
  serviceDate: string | null;
  notes: string | null;
}

export interface PlaylistUpdatePayload {
  id: number;
  name: string;
  serviceDate: string | null;
  notes: string | null;
  expectedRevision: number;
}

/** `itemType`: "song" (`songId`) | "passage" (libro canónico 1 a 66, capítulo, versículos y Biblia preferida) | "note" (`title`, `note`, `clearOnActivate`). */
export interface PlaylistNewItemDto {
  itemType: string;
  songId: number | null;
  bookNumber: number | null;
  chapter: number | null;
  verseStart: number | null;
  verseEnd: number | null;
  bibleAbbreviation: string | null;
  title: string | null;
  note: string | null;
  clearOnActivate: boolean;
}

/** `position` nulo = al final. No exige revisión (añadir no destruye nada), pero sube la de la lista. */
export interface PlaylistAddItemPayload {
  playlistId: number;
  item: PlaylistNewItemDto;
  position: number | null;
}

export interface PlaylistMoveItemPayload {
  playlistId: number;
  itemId: number;
  toIndex: number;
  expectedRevision: number;
}

export interface PlaylistRemoveItemPayload {
  playlistId: number;
  itemId: number;
  expectedRevision: number;
}

/** Lo que va nulo no se toca (`note` vacío la borra). */
export interface PlaylistUpdateItemPayload {
  playlistId: number;
  itemId: number;
  title: string | null;
  note: string | null;
  clearOnActivate: boolean | null;
  bibleAbbreviation: string | null;
  expectedRevision: number;
}

export interface PlaylistDuplicatePayload {
  id: number;
  name: string;
  serviceDate: string | null;
}

/** Exige la revisión vigente (v14): con una caducada falla con `playlist.conflict` y no borra nada. */
export interface PlaylistDeletePayload {
  id: number;
  expectedRevision: number;
}

/** `kind`: "created" | "updated" | "itemAdded" | "itemMoved" | "itemRemoved" | "itemUpdated" | "duplicated" | "deleted". Solo ids y conteos. */
export interface PlaylistChangedPayload {
  commandId: string;
  playlistId: number;
  revision: number;
  kind: string;
  itemId: number | null;
  sourceId: number | null;
  copied: number;
  skipped: number;
}

/** Reply de `playlist.getSettings` y cuerpo de `playlist.setSettings`. */
export interface PlaylistSettingsPayload {
  onItemEnd: PlaylistOnItemEnd;
}

/** `durationSec`: 0 = «hasta quitarla» (tope de 30 min); si no, de 5 a 1800 s (el panel ofrece 15, 30, 60, 120 y 300). Mostrar REEMPLAZA a la alerta que hubiera. */
export interface AlertShowPayload {
  text: string;
  mode: AlertMode;
  durationSec: number;
}

/** Una alerta guardada. `lastUsedAt` (ISO-8601 UTC) nulo = nunca se mostró. */
export interface SavedAlertDto {
  id: number;
  text: string;
  pinned: boolean;
  useCount: number;
  lastUsedAt: string | null;
}

/** Reply de `alert.list`: las fijadas (orden alfabético) y las recientes (como mucho 10 de los últimos 14 días, la más nueva primero). */
export interface AlertListPayload {
  pinned: SavedAlertDto[];
  recent: SavedAlertDto[];
}

/** Crea (sin `id`) o edita una alerta guardada y la fija o desfija (máximo 30 fijadas: `alert.full`). */
export interface AlertSavePayload {
  id: number | null;
  text: string;
  pinned: boolean;
}

export interface AlertDeletePayload {
  id: number;
}

/** Resultado de `alert.save` / `alert.delete`, correlacionado por el id del comando. `kind`: "saved" | "deleted". Solo ids. */
export interface AlertChangedPayload {
  commandId: string;
  alertId: number;
  kind: string;
}

/** Reply de `alert.getSettings` y cuerpo de `alert.setSettings`. */
export interface AlertSettingsPayload {
  mode: AlertMode;
  durationSec: number;
  theme: AlertTheme;
  /** Interruptor de seguridad del desplazamiento (v16): apagado, solo vale la cinta estática. */
  tickerEnabled: boolean;
}

/** Tipo de un elemento de la biblioteca de medios (v17). */
export type MediaItemKind = "video" | "audio" | "image";
/** `pending`: copiado y sin verificar; `ok`: se reproduce en este equipo; `unsupported`: no se puede proyectar (`statusCode` dice por qué); `missing`: el archivo desapareció. */
export type MediaStatus = "pending" | "ok" | "unsupported" | "missing";

export interface MediaFolderDto {
  id: number;
  name: string;
  itemCount: number;
}

/**
 * Un elemento de la biblioteca. `fileUrl`: URL `file:///` de la COPIA local (nula si falta); la usa el panel para verificar y sacar la miniatura. `thumbnailUrl` nula = sin miniatura aún.
 * `statusCode`/`statusMessage`: motivo si no se puede usar o aviso si se puede (p. ej. HEVC). `durationMs` nulo = desconocida.
 */
export interface MediaItemDto {
  id: number;
  kind: MediaItemKind;
  name: string;
  folderId: number | null;
  originalFileName: string;
  ext: string;
  bytes: number;
  durationMs: number | null;
  width: number | null;
  height: number | null;
  container: string | null;
  videoCodec: string | null;
  audioCodec: string | null;
  hasAudio: boolean;
  status: MediaStatus;
  statusCode: string | null;
  statusMessage: string | null;
  fileUrl: string | null;
  thumbnailUrl: string | null;
  revision: number;
  createdAt: string;
  updatedAt: string;
}

/** `folderId` nulo = todas las carpetas; `onlyUnfiled` = solo los que no están en ninguna; `query` filtra por nombre; como mucho 500 por consulta. */
export interface MediaListRequest {
  folderId: number | null;
  onlyUnfiled: boolean;
  kind: MediaItemKind | null;
  query: string | null;
  limit: number | null;
}

/** `totalBytes`/`totalItems`: toda la biblioteca; `freeBytes`: espacio libre del disco de datos (−1 si no se sabe). */
export interface MediaListPayload {
  folders: MediaFolderDto[];
  items: MediaItemDto[];
  truncated: boolean;
  totalItems: number;
  totalBytes: number;
  freeBytes: number;
}

export interface MediaGetRequest {
  id: number;
}

/** Vuelve al instante: la copia corre en segundo plano (una a la vez). `path` es la ruta COMPLETA del archivo (local, extraíble o de red). */
export interface MediaImportPayload {
  path: string;
  name: string | null;
  folderId: number | null;
}

export interface MediaRenamePayload {
  id: number;
  name: string;
}

export interface MediaMovePayload {
  id: number;
  folderId: number | null;
}

/** Se rechaza (`media.inUse`) si es el fondo que está en la salida. */
export interface MediaDeletePayload {
  id: number;
}

export interface MediaCreateFolderPayload {
  name: string;
}

export interface MediaRenameFolderPayload {
  id: number;
  name: string;
}

export interface MediaDeleteFolderPayload {
  id: number;
}

/** Lo que el panel averiguó al cargar la copia en su reproductor oculto. `errorCode`: "unsupported" | "audioUnsupported" | "decode" | "timeout". */
export interface MediaReportProbePayload {
  mediaId: number;
  ok: boolean;
  durationMs: number | null;
  width: number | null;
  height: number | null;
  errorCode: string | null;
}

/** Miniatura JPEG (≤ 200 KB, ≤ 640×360) en base64. */
export interface MediaSetThumbnailPayload {
  mediaId: number;
  jpegBase64: string;
}

export interface MediaSetBackgroundPayload {
  mediaId: number;
  muted: boolean;
  transitionMs: number;
}

/** Avance de la importación (`importId` = id del comando `media.import`). Solo bytes: ni nombres ni rutas. */
export interface MediaImportProgressPayload {
  importId: string;
  phase: string;
  doneBytes: number;
  totalBytes: number;
}

/** Un duplicado NO es un fallo técnico: `errorCode` = "media.duplicate" con `duplicateOfId` y `duplicateOfName`. `warningCode`: importó con un aviso (HEVC, AC-3…). */
export interface MediaImportFinishedPayload {
  importId: string;
  ok: boolean;
  errorCode: string | null;
  errorMessage: string | null;
  mediaId: number | null;
  warningCode: string | null;
  warningMessage: string | null;
  duplicateOfId: number | null;
  duplicateOfName: string | null;
}

/** `kind`: "added" | "updated" | "deleted" | "folders" | "verified". `count`: faltantes al verificar. */
export interface MediaChangedPayload {
  commandId: string;
  kind: string;
  mediaId: number | null;
  count: number;
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
  "output.retryBackground": EmptyPayload;
  "output.reportBackgroundFailure": OutputReportBackgroundFailurePayload;
  "settings.setAutoRestore": SetAutoRestorePayload;
  "settings.setDisplayLostPolicy": SetDisplayLostPolicyPayload;
  "settings.setPanelAlertOnOutput": SetPanelAlertOnOutputPayload;
  "bible.import": BibleImportPayload;
  "bible.setAttribution": BibleSetAttributionPayload;
  "bible.setSettings": BibleSetSettingsPayload;
  "song.setSettings": SongSettingsPayload;
  "song.save": SongSavePayload;
  "song.restoreRevision": SongRestoreRevisionPayload;
  "song.delete": SongDeletePayload;
  "playlist.setSettings": PlaylistSettingsPayload;
  "playlist.create": PlaylistCreatePayload;
  "playlist.update": PlaylistUpdatePayload;
  "playlist.addItem": PlaylistAddItemPayload;
  "playlist.moveItem": PlaylistMoveItemPayload;
  "playlist.removeItem": PlaylistRemoveItemPayload;
  "playlist.updateItem": PlaylistUpdateItemPayload;
  "playlist.duplicate": PlaylistDuplicatePayload;
  "playlist.delete": PlaylistDeletePayload;
  "alert.show": AlertShowPayload;
  "alert.hide": EmptyPayload;
  "alert.setSettings": AlertSettingsPayload;
  "alert.save": AlertSavePayload;
  "alert.delete": AlertDeletePayload;
  "media.import": MediaImportPayload;
  "media.cancelImport": EmptyPayload;
  "media.rename": MediaRenamePayload;
  "media.move": MediaMovePayload;
  "media.delete": MediaDeletePayload;
  "media.createFolder": MediaCreateFolderPayload;
  "media.renameFolder": MediaRenameFolderPayload;
  "media.deleteFolder": MediaDeleteFolderPayload;
  "media.reportProbe": MediaReportProbePayload;
  "media.setThumbnail": MediaSetThumbnailPayload;
  "media.verifyLibrary": EmptyPayload;
  "media.setBackground": MediaSetBackgroundPayload;
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
  "song.changed": SongChangedPayload;
  "playlist.changed": PlaylistChangedPayload;
  "alert.changed": AlertChangedPayload;
  "media.importProgress": MediaImportProgressPayload;
  "media.importFinished": MediaImportFinishedPayload;
  "media.changed": MediaChangedPayload;
}

/** Payload de cada query y de su reply. */
export interface QueryPayloads {
  "display.list": { request: EmptyPayload; reply: DisplayListPayload };
  "output.getState": { request: EmptyPayload; reply: OutputStateChangedPayload };
  "database.getStatus": { request: EmptyPayload; reply: DatabaseStatusPayload };
  "settings.getStatus": { request: EmptyPayload; reply: SettingsStatusPayload };
  "bible.list": { request: EmptyPayload; reply: BibleListPayload };
  "bible.lookup": { request: BibleLookupRequest; reply: BiblePassagePayload };
  "bible.chapter": { request: BibleChapterRequest; reply: BiblePassagePayload };
  "bible.search": { request: BibleSearchRequest; reply: BibleSearchPayload };
  "bible.history": { request: BibleHistoryRequest; reply: BibleHistoryPayload };
  "bible.suggest": { request: BibleSuggestRequest; reply: BibleSuggestPayload };
  "bible.getSettings": { request: EmptyPayload; reply: BibleSettingsPayload };
  "song.search": { request: SongSearchRequest; reply: SongSearchPayload };
  "song.list": { request: EmptyPayload; reply: SongListPayload };
  "song.get": { request: SongGetRequest; reply: SongPayload };
  "song.parseText": { request: SongParseTextRequest; reply: SongParseTextPayload };
  "song.history": { request: SongHistoryRequest; reply: SongHistoryPayload };
  "song.getSettings": { request: EmptyPayload; reply: SongSettingsPayload };
  "playlist.list": { request: EmptyPayload; reply: PlaylistListPayload };
  "playlist.get": { request: PlaylistGetRequest; reply: PlaylistPayload };
  "playlist.getSettings": { request: EmptyPayload; reply: PlaylistSettingsPayload };
  "alert.getSettings": { request: EmptyPayload; reply: AlertSettingsPayload };
  "alert.list": { request: EmptyPayload; reply: AlertListPayload };
  "media.list": { request: MediaListRequest; reply: MediaListPayload };
  "media.get": { request: MediaGetRequest; reply: MediaItemDto };
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




