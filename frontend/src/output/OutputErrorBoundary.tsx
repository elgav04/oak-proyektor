import { Component, type ErrorInfo, type ReactNode } from "react";

interface Props {
  /** Identifica el snapshot actual. Cuando cambia después de un fallo, se vuelve a intentar renderizar. */
  resetKey: number | null;
  /** Qué mostrar mientras el snapshot actual no se pueda pintar. */
  fallback: () => ReactNode;
  children: ReactNode;
}

interface State {
  failed: boolean;
  /** resetKey con el que falló; null mientras no se sepa. */
  failedKey: number | null;
}

/**
 * Un error de render en la salida NO debe dejar la pantalla en negro: React desmonta todo el árbol ante un error sin
 * boundary y no se recupera ni con un snapshot posterior válido. Aquí se muestra el último estado pintado y, cuando
 * llega un snapshot distinto al que falló, se reintenta.
 */
export class OutputErrorBoundary extends Component<Props, State> {
  override state: State = { failed: false, failedKey: null };
  private renderedKey: number | null = null;

  static getDerivedStateFromError(): Partial<State> {
    return { failed: true };
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error("[output] error de render; se conserva el último estado pintado", error, info.componentStack);
    this.setState({ failedKey: this.renderedKey });
  }

  override componentDidUpdate(): void {
    // Solo se reintenta si ya sabemos con qué snapshot falló y llegó uno distinto: evita reintentar el mismo en bucle.
    if (this.state.failed && this.state.failedKey !== null && this.state.failedKey !== this.props.resetKey) {
      this.setState({ failed: false, failedKey: null });
    }
  }

  override render(): ReactNode {
    this.renderedKey = this.props.resetKey;
    return this.state.failed ? this.props.fallback() : this.props.children;
  }
}
