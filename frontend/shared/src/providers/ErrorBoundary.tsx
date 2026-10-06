import { Component, type ErrorInfo, type ReactNode } from 'react';
import { ApiError } from '../api/client';
import { ErrorState } from '../components/ui/States';

type Props = { children: ReactNode; fallback?: (error: Error, reset: () => void) => ReactNode };
type State = { error: Error | null };

/** Catches render errors so one broken screen never blanks the whole shell. */
export class ErrorBoundary extends Component<Props, State> {
  state: State = { error: null };

  static getDerivedStateFromError(error: Error): State {
    return { error };
  }

  componentDidCatch(error: Error, info: ErrorInfo): void {
    // Telemetry hook point (browser OpenTelemetry is a later-phase concern).
    console.error('Unhandled UI error', error, info.componentStack);
  }

  private reset = () => this.setState({ error: null });

  render(): ReactNode {
    const { error } = this.state;
    if (!error) return this.props.children;
    if (this.props.fallback) return this.props.fallback(error, this.reset);

    return (
      <div className="p-6">
        <ErrorState
          title="Beklenmeyen bir hata oluştu"
          description="Sayfa görüntülenirken bir sorun oluştu. Tekrar deneyebilirsiniz."
          correlationId={error instanceof ApiError ? error.correlationId : undefined}
          onRetry={this.reset}
        />
      </div>
    );
  }
}
