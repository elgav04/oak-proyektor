import { Component, type ErrorInfo, type ReactNode } from "react";

interface Props {
  children: ReactNode;
}
interface State {
  failed: boolean;
}

/** Un fallo de render en el panel no debe dejar la pantalla en blanco; la salida es otra ventana y no se ve afectada. */
export class ErrorBoundary extends Component<Props, State> {
  override state: State = { failed: false };

  static getDerivedStateFromError(): State {
    return { failed: true };
  }

  override componentDidCatch(error: Error, info: ErrorInfo): void {
    console.error("[control] error de render", error, info.componentStack);
  }

  override render(): ReactNode {
    if (!this.state.failed) return this.props.children;
    return (
      <div className="p-6">
        <p className="mb-4 text-lg font-semibold">El panel tuvo un error. La salida sigue funcionando.</p>
        <button
          className="rounded-md bg-neutral-900 px-4 py-2 text-white"
          onClick={() => {
            this.setState({ failed: false });
          }}
        >
          Reintentar
        </button>
      </div>
    );
  }
}
