import { Component, useEffect, useLayoutEffect, useRef, useState, type ReactNode } from "react";
import type { AlertOverlay } from "@/ipc/protocol";
import { ALERT_FADE_MS, ALERT_SIDE_PADDING, alertBandStyle, alertTextStyle, fitsInLines, tickerDurationSec } from "../alert";
import { CANVAS_WIDTH } from "../canvas";
import { fitFontSize } from "../fit";

/**
 * Texto de la cinta estática. Con ajuste automático la letra baja desde `fontSize` hasta `minFontSize` (≤ 9 mediciones) hasta caber en `maxLines` renglones; si ni el mínimo
 * cabe, la cinta crece (hasta su tope de altura). Depende de PRIMITIVOS, no del objeto del snapshot (que llega nuevo cada vez): un snapshot que repite la misma alerta no vuelve a medir.
 */
function BandText({ alert }: { alert: AlertOverlay }) {
  const ref = useRef<HTMLDivElement>(null);
  const { id, text } = alert;
  const t = alert.theme;
  useLayoutEffect(() => {
    const el = ref.current;
    if (!el) return;
    if (!t.autoFit) {
      el.style.fontSize = `${String(t.fontSize)}px`;
      return;
    }
    const size = fitFontSize(t.minFontSize, t.fontSize, (candidate) => {
      el.style.fontSize = `${String(candidate)}px`;
      return fitsInLines(el.offsetHeight, candidate, t.maxLines);
    });
    el.style.fontSize = `${String(size)}px`;
  }, [id, text, t.autoFit, t.fontSize, t.minFontSize, t.maxLines, t.fontFamily, t.outlineWidth]);
  return (
    <div ref={ref} className="alert-text" data-alert-mode="band" style={{ ...alertTextStyle(t, t.fontSize), overflowWrap: "anywhere" }}>
      {text}
    </div>
  );
}

/**
 * Texto con desplazamiento: una sola línea que cruza el lienzo de derecha a izquierda en bucle, a velocidad constante. La duración se calcula UNA vez al montar (con el ancho medido del
 * texto): `transform` en el compositor, sin tocar el layout por fotograma. El componente lleva la CLAVE del id de la alerta (ver `AlertBand`): un snapshot que repite la misma alerta
 * no remonta el elemento ni reinicia la animación.
 */
function TickerText({ alert }: { alert: AlertOverlay }) {
  const trackRef = useRef<HTMLDivElement>(null);
  const [duration, setDuration] = useState<number | null>(null);
  const t = alert.theme;
  useLayoutEffect(() => {
    const width = trackRef.current?.scrollWidth ?? 0;
    setDuration(tickerDurationSec(width, t.tickerSpeed));
  }, [alert.text, t.tickerSpeed, t.fontSize, t.fontFamily, t.outlineWidth]);
  return (
    // El desplazamiento cruza TODO el ancho del lienzo: compensa el relleno lateral de la cinta.
    <div className="alert-ticker" data-alert-mode="ticker" style={{ width: CANVAS_WIDTH, margin: `0 -${String(ALERT_SIDE_PADDING)}px`, overflow: "hidden", whiteSpace: "nowrap" }}>
      <div
        ref={trackRef}
        className="alert-ticker-track"
        data-duration={duration === null ? "" : String(duration)}
        style={{
          ...alertTextStyle(t, t.fontSize),
          display: "inline-block",
          willChange: "transform",
          transform: `translateX(${String(CANVAS_WIDTH)}px)`,   // antes de medir, fuera de la pantalla: nunca se ve un fotograma en el sitio equivocado
          animation: duration === null ? "none" : `alert-marquee ${String(duration)}s linear infinite`,
        }}
      >
        {alert.text}
      </div>
    </div>
  );
}

/**
 * Mantiene la alerta en el DOM mientras se desvanece. Devuelve la alerta a pintar (la última recibida, o la que sale) y si debe verse opaca. Un snapshot con otra alerta (otro id)
 * la REEMPLAZA en el sitio, sin salir y volver a entrar (nunca hay hueco); solo al aparecer desde la nada o al quedar sin alerta hay fundido.
 */
function usePresence(alert: AlertOverlay | null): { held: AlertOverlay | null; shown: boolean } {
  const [held, setHeld] = useState<AlertOverlay | null>(alert);
  const [leaving, setLeaving] = useState(false);
  const [entered, setEntered] = useState(false);
  if (alert !== null && alert !== held) setHeld(alert);   // estado derivado de props: la alerta nueva se pinta en este mismo commit
  if (alert !== null && leaving) setLeaving(false);

  useEffect(() => {
    if (alert !== null) return;
    if (held === null) return;
    setLeaving(true);
    const timer = setTimeout(() => {
      setHeld(null);
      setLeaving(false);
      setEntered(false);
    }, ALERT_FADE_MS + 60);
    return () => {
      clearTimeout(timer);
    };
  }, [alert, held]);

  const present = held !== null;
  useEffect(() => {
    if (!present) return;
    // Aparece con opacidad 0 y pasa a 1 en el siguiente fotograma: el fundido de entrada.
    const timer = setTimeout(() => {
      setEntered(true);
    }, 16);
    return () => {
      clearTimeout(timer);
    };
  }, [present]);

  return { held, shown: present && entered && !leaving && alert !== null };
}

/**
 * Capa de la alerta (capa 3). La cinta es UN elemento persistente mientras haya alerta: solo cambia su contenido (con la clave del id) y la opacidad (compositor). No toca el contenido
 * ni el fondo: vive en su propio contenedor dentro de `OverlayLayer`.
 */
export function AlertLayer({ alert }: { alert: AlertOverlay | null }) {
  if (alert !== null && typeof alert.text !== "string") throw new Error("La alerta no tiene un texto válido");   // falla AQUÍ, dentro del límite de errores de la alerta
  const { held, shown } = usePresence(alert);
  if (held === null) return null;
  return (
    <div
      className="alert-band"
      data-alert-id={held.id}
      data-alert-position={held.theme.position}
      style={{ ...alertBandStyle(held.theme), opacity: shown ? 1 : 0, transition: `opacity ${String(ALERT_FADE_MS)}ms linear` }}
    >
      {held.mode === "ticker" ? <TickerText key={held.id} alert={held} /> : <BandText key={held.id} alert={held} />}
    </div>
  );
}

interface BoundaryProps {
  /** Identifica la alerta actual: tras un fallo solo se reintenta cuando llega una distinta. */
  resetKey: string | null;
  children: ReactNode;
}

interface BoundaryState {
  failed: boolean;
  failedKey: string | null | undefined;
}

/**
 * Límite de errores PROPIO de la alerta. Un error al pintarla oculta SOLO la alerta: no sustituye el contenido por «lo último pintado» ni toca el fondo (con el límite general, un error en la
 * capa 3 habría afectado al texto proyectado). Se reintenta cuando llega otra alerta. No registra el texto (solo el nombre del error).
 */
export class AlertErrorBoundary extends Component<BoundaryProps, BoundaryState> {
  override state: BoundaryState = { failed: false, failedKey: undefined };
  private renderedKey: string | null = null;

  static getDerivedStateFromError(): Partial<BoundaryState> {
    return { failed: true };
  }

  override componentDidCatch(error: Error): void {
    console.error("[output] error de render de la alerta; se oculta (el contenido y el fondo no se tocan):", error.name);
    this.setState({ failedKey: this.renderedKey });
  }

  override componentDidUpdate(): void {
    if (this.state.failed && this.state.failedKey !== undefined && this.state.failedKey !== this.props.resetKey) {
      this.setState({ failed: false, failedKey: undefined });
    }
  }

  override render(): ReactNode {
    this.renderedKey = this.props.resetKey;
    return this.state.failed ? null : this.props.children;
  }
}
