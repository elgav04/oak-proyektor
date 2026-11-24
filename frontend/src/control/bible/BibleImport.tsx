import { useState } from "react";
import { useStore } from "zustand";
import { Button } from "@/components/ui/button";
import type { BibleImportReportDto } from "@/ipc/protocol";
import type { BibleController } from "./controller";
import { allowKindFor, checkImportForm, EMPTY_FORM, MAX_ABBREVIATION_LENGTH, NUMBERING_PROFILES, type ImportController, type ImportForm } from "./importController";

const PHASE_LABELS: Record<string, string> = {
  reading: "Leyendo el archivo",
  cleaning: "Limpiando el texto",
  saving: "Guardando en la base de datos",
  validating: "Validando",
};

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return <label className="flex flex-col gap-1 text-sm">{label}{children}</label>;
}

const STANDARD_TOTAL = 31102;

/** Resumen del informe: solo conteos y referencias (el backend nunca manda texto bíblico). */
function ReportView({ report }: { report: BibleImportReportDto }) {
  const difference = report.verseTotalDifference;
  return (
    <div className="flex flex-col gap-2 text-sm">
      <p>
        <span className="font-semibold">{report.created ? "Biblia nueva" : "Biblia actualizada en el sitio"}</span>
        {" — "}
        {report.verses.toLocaleString("es")} versículos en {report.books} libros y {report.chapters} capítulos ({(report.elapsedMs / 1000).toFixed(1)} s).
      </p>
      {!report.created && (
        <p data-testid="modified-count">
          Textos modificados: <b>{report.updated.toLocaleString("es")}</b> de los {report.previousVerses.toLocaleString("es")} que había
          {report.previousVerses > 0 ? ` (${((100 * report.updated) / report.previousVerses).toLocaleString("es", { maximumFractionDigits: 2 })} %)` : ""}.
        </p>
      )}
      <p data-testid="backup-path">
        {report.backupPath ? <>Copia de seguridad previa: <span className="break-all font-mono text-xs">{report.backupPath}</span> (se conservan las últimas 5).</> : "Copia de seguridad previa: no hizo falta (no se modificó ni borró nada)."}
      </p>
      <ul className="list-disc pl-5">
        <li>De la fuente: {report.sourceVerses.toLocaleString("es")}. Nuevos: {report.inserted.toLocaleString("es")}. Actualizados: {report.updated.toLocaleString("es")}. Sin cambios: {report.unchanged.toLocaleString("es")}. Eliminados: {report.deleted.toLocaleString("es")}.</li>
        <li>
          Total frente al canon habitual ({STANDARD_TOTAL.toLocaleString("es")}):{" "}
          {difference === 0 ? "igual" : `${difference > 0 ? "+" : ""}${String(difference)} (numeración distinta o Biblia incompleta)`}.
        </li>
        <li data-testid="profile-line">Perfil de numeración aplicado: {report.numberingProfile}{report.numberingProfileKept ? " (conservado: no se indicó otro)" : ""}. Versículos limpiados: {report.cleanedVerses}. Unidos de varios trozos: {report.joinedVerses}. Vacíos: {report.emptyVerses}.
          {report.discardedVerses > 0 ? ` Descartados (numeración inválida o fuera del canon): ${String(report.discardedVerses)}.` : ""}
          {report.duplicateChaptersDifferent > 0 ? ` ¡${String(report.duplicateChaptersDifferent)} capítulo(s) repetido(s) con texto DISTINTO!` : ""}
        </li>
        <li className={report.ftsIntegrityOk ? "" : "font-bold text-red-700"}>
          Índice de búsqueda: {report.ftsIntegrityOk ? "correcto" : "FALLÓ la comprobación (la búsqueda de texto puede dar resultados incompletos)"}.
        </li>
      </ul>
      {report.affectedItems.length > 0 && (
        <div className="rounded border border-amber-600 bg-amber-50 p-2">
          <p className="font-semibold">Estos elementos apuntaban a versículos que se eliminaron:</p>
          <ul className="list-disc pl-5">
            {report.affectedItems.map((a) => (
              <li key={`${a.kind}-${String(a.id)}`}>{a.kind === "playlist" ? `Servicio ${String(a.playlistId ?? "")}` : "Historial"}: {a.reference}</li>
            ))}
          </ul>
        </div>
      )}
      {report.warnings.length > 0 && (
        <div className="rounded border border-amber-600 bg-amber-50 p-2">
          <p className="font-semibold">Avisos ({report.warnings.length}):</p>
          <ul className="max-h-48 list-disc overflow-y-auto pl-5">
            {report.warnings.map((w, i) => <li key={`${String(i)}-${w}`}>{w}</li>)}
          </ul>
        </div>
      )}
    </div>
  );
}

/**
 * Importar una Biblia desde un archivo (JSON por capítulos). El archivo se elige pegando su ruta completa (la página no puede abrir un selector de archivos que
 * entregue la ruta real). Importa el backend en segundo plano; aquí solo se ve el avance y el informe.
 */
export function BibleImportSection({ importer, bibles: controller, connected }: { importer: ImportController; bibles: BibleController; connected: boolean }) {
  const view = useStore(importer.store);
  const installed = useStore(controller.store).bibles;
  const [form, setForm] = useState<ImportForm>(EMPTY_FORM);
  const [profileTouched, setProfileTouched] = useState(false);
  // Confirmación para repetir una importación RECHAZADA saltándose la protección (se pide cada vez).
  const [confirmRisk, setConfirmRisk] = useState(false);

  const abbreviation = form.abbreviation.trim();
  // Sin distinguir mayúsculas (como el backend): «rvr1960» es la MISMA Biblia que «RVR1960».
  const existing = installed.find((b) => b.abbreviation === abbreviation) ?? installed.find((b) => b.abbreviation.toLowerCase() === abbreviation.toLowerCase());
  // Al actualizar una Biblia ya instalada se parte de SUS datos: nombre, idioma y, sobre todo, el perfil de numeración (reimportar sin él cambiaría la numeración guardada).
  const effective: ImportForm = {
    ...form,
    name: form.name.trim().length > 0 ? form.name : (existing?.name ?? ""),
    language: form.language.trim().length > 0 ? form.language : (existing?.language ?? ""),
    numberingProfile: profileTouched ? form.numberingProfile : (existing ? (existing.numberingProfile ?? "none") : form.numberingProfile),
  };
  const check = checkImportForm(effective, installed);
  const running = view.status === "running";
  const set = (patch: Partial<ImportForm>): void => { setForm({ ...form, ...patch }); };

  const percent = view.total > 0 ? Math.min(100, Math.round((view.done / view.total) * 100)) : 0;

  return (
    <section className="flex flex-col gap-3" aria-label="Importar una Biblia" data-testid="bible-import">
      <h3 className="text-lg font-semibold">Importar una Biblia</h3>
      <p className="text-sm text-neutral-600">
        Formato: JSON por capítulos (el aprobado como fuente). Pega la <b>ruta completa</b> del archivo. La importación corre en segundo plano y es todo o nada: si falla, no se cambia nada.
        Usa una abreviatura nueva para una Biblia nueva; la misma abreviatura de una ya instalada la ACTUALIZA.
      </p>

      {running && (
        <div role="status" className="flex flex-col gap-1 rounded border-2 border-blue-700 bg-blue-50 p-3 text-sm" data-testid="import-progress">
          <p className="font-semibold">Importando «{view.abbreviation}»: {PHASE_LABELS[view.phase ?? ""] ?? view.phase}… {percent}%</p>
          <div className="h-3 w-full overflow-hidden rounded bg-blue-200"><div className="h-full bg-blue-700" style={{ width: `${String(percent)}%` }} /></div>
          <p className="text-xs text-neutral-600">Puedes seguir usando el panel. No cierres la aplicación hasta que termine.</p>
        </div>
      )}

      {view.status === "finished" && view.result && (
        <div
          role="status"
          data-testid="import-result"
          className={`flex flex-col gap-2 rounded border-2 p-3 ${view.result.ok ? "border-green-700 bg-green-50" : "border-red-700 bg-red-50"}`}
        >
          {view.result.ok && view.result.report ? (
            <>
              <p className="font-bold">Importación de «{view.result.abbreviation}» terminada.</p>
              <ReportView report={view.result.report} />
            </>
          ) : (
            <>
              <p className="font-bold">La importación de «{view.result.abbreviation}» falló. No se cambió nada.</p>
              <p className="text-sm">{view.result.errorMessage ?? "Error desconocido."} {view.result.errorCode && <span className="text-neutral-600">({view.result.errorCode})</span>}</p>
              {view.canRetryAllowing && (
                <div className="flex flex-col gap-2 rounded border-2 border-amber-600 bg-amber-50 p-3 text-sm" data-testid="override-box">
                  <p>
                    Esto es una <b>protección</b>: casi siempre significa que el archivo no es el correcto (otra traducción con la misma abreviatura, o un archivo incompleto). No se ha cambiado nada.
                    Antes de modificar o borrar, el programa hace una copia de seguridad de la base de datos.
                  </p>
                  <label className="flex cursor-pointer items-start gap-2">
                    <input type="checkbox" className="mt-1" checked={confirmRisk} onChange={(e) => { setConfirmRisk(e.target.checked); }} />
                    <span>Sé lo que hago: el archivo es el correcto y quiero {allowKindFor(view.result.errorCode) === "deletion" ? "que se eliminen esos versículos o libros" : "sobrescribir los textos de esa Biblia"}.</span>
                  </label>
                  <Button
                    variant="outline"
                    className="w-fit"
                    disabled={!connected || !confirmRisk}
                    onClick={() => {
                      if (importer.retryAllowing()) setConfirmRisk(false);
                    }}
                  >
                    Importar de todos modos
                  </Button>
                </div>
              )}
            </>
          )}
          <Button variant="outline" className="w-fit" onClick={() => { setConfirmRisk(false); importer.dismiss(); }}>Cerrar informe</Button>
        </div>
      )}

      <fieldset className="flex flex-col gap-3" disabled={!connected || running}>
        <Field label="Ruta del archivo (JSON), por ejemplo C:\Biblias\version.json">
          <input className="h-10 rounded-md border-2 p-2 font-mono text-sm" value={form.path} placeholder="C:\Biblias\version.json" onChange={(e) => { set({ path: e.target.value }); }} />
        </Field>
        <div className="grid grid-cols-1 gap-3 sm:grid-cols-3">
          <Field label={`Abreviatura (máx. ${String(MAX_ABBREVIATION_LENGTH)})`}>
            <input className="h-10 rounded-md border-2 p-2" maxLength={MAX_ABBREVIATION_LENGTH} value={form.abbreviation} placeholder="NVI" onChange={(e) => { set({ abbreviation: e.target.value, confirmUpdate: false }); }} />
          </Field>
          <Field label="Nombre">
            <input className="h-10 rounded-md border-2 p-2" value={form.name} placeholder={existing?.name ?? "Nueva Versión Internacional"} onChange={(e) => { set({ name: e.target.value }); }} />
          </Field>
          <Field label="Idioma">
            <input className="h-10 rounded-md border-2 p-2" value={form.language} placeholder={existing?.language ?? "es"} onChange={(e) => { set({ language: e.target.value }); }} />
          </Field>
        </div>
        <Field label="Perfil de numeración">
          <select
            className="h-10 rounded-md border-2 p-2"
            value={effective.numberingProfile}
            onChange={(e) => { setProfileTouched(true); set({ numberingProfile: e.target.value }); e.currentTarget.blur(); }}
          >
            {NUMBERING_PROFILES.map((p) => <option key={p.id} value={p.id}>{p.label}</option>)}
          </select>
        </Field>
        <Field label="Aviso de derechos (opcional; el texto que tú escribas. Vacío = no tocar el actual)">
          <textarea className="min-h-16 rounded-md border-2 p-2" value={form.attribution} onChange={(e) => { set({ attribution: e.target.value }); }} />
        </Field>

        {existing && (
          <label className="flex cursor-pointer items-start gap-2 rounded border-2 border-amber-600 bg-amber-50 p-3 text-sm" data-testid="confirm-update">
            <input type="checkbox" className="mt-1" checked={form.confirmUpdate} onChange={(e) => { set({ confirmUpdate: e.target.checked }); }} />
            <span>
              <b>Ya hay una Biblia «{existing.abbreviation}»</b> ({existing.name}, {existing.verses.toLocaleString("es")} versículos). Importar la ACTUALIZA en el sitio: si el archivo es otra
              traducción, se sobrescribe TODO su texto, y los versículos que el archivo no traiga se eliminan. Confirmo que es el archivo correcto.
            </span>
          </label>
        )}
      </fieldset>

      {check.errors.length > 0 && form.path.length + form.abbreviation.length > 0 && (
        <ul className="list-disc pl-5 text-sm text-red-700" data-testid="import-errors">{check.errors.map((e) => <li key={e}>{e}</li>)}</ul>
      )}
      {check.warnings.length > 0 && (
        <ul className="list-disc pl-5 text-sm text-amber-900" data-testid="import-warnings">{check.warnings.map((w) => <li key={w}>{w}</li>)}</ul>
      )}

      <div>
        <Button
          size="lg"
          disabled={!connected || running || check.errors.length > 0}
          onClick={() => {
            if (importer.start(effective)) {
              setForm({ ...EMPTY_FORM, language: form.language });
              setProfileTouched(false);
            }
          }}
        >
          Importar
        </Button>
      </div>
    </section>
  );
}
