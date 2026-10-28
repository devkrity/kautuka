import '@fontsource/source-sans-3/latin-400.css';
import '@fontsource/source-sans-3/latin-600.css';
import '@fontsource/source-sans-3/latin-700.css';
import '@fontsource/ibm-plex-mono/latin-400.css';
import React from 'react';
import ReactDOM from 'react-dom/client';
import App from './app/App';
import './index.css';
import { QueryProvider } from './providers/QueryProvider';
import { Button } from './components/ui/button';

const rootElementId = ['ro', 'ot'].join('');

type GlobalErrorBoundaryProps = {
  children: React.ReactNode;
};

type GlobalErrorBoundaryState = {
  error: Error | null;
  componentStack: string;
  crashId: string;
  retryCount: number;
};

class GlobalErrorBoundary extends React.Component<
  GlobalErrorBoundaryProps,
  GlobalErrorBoundaryState
> {
  constructor(props: GlobalErrorBoundaryProps) {
    super(props);
    this.state = { error: null, componentStack: '', crashId: '', retryCount: 0 };
  }

  static getDerivedStateFromError(error: Error): GlobalErrorBoundaryState {
    return {
      error,
      componentStack: '',
      crashId: `ui-${Date.now().toString(36)}`,
      retryCount: 0,
    };
  }

  componentDidCatch(error: Error, errorInfo: React.ErrorInfo) {
    this.setState((state) => ({
      componentStack: errorInfo.componentStack ?? state.componentStack,
    }));

    void errorInfo;
  }

  private handleRecover = () => {
    this.setState((state) => ({
      error: null,
      componentStack: '',
      crashId: '',
      retryCount: state.retryCount + 1,
    }));
  };

  render() {
    if (this.state.error) {
      return (
        <div className="global-crash-screen">
          <h2>GLOBAL CRASH</h2>
          <p>
            Crash ID: {this.state.crashId} | Path: {globalThis.location.pathname}
          </p>
          <pre>{String(this.state.error.message || this.state.error)}</pre>
          <pre>{String(this.state.error.stack)}</pre>
          {this.state.componentStack ? <pre>{this.state.componentStack}</pre> : null}
          <Button type="button" onClick={this.handleRecover}>
            Try Recover UI
          </Button>
        </div>
      );
    }
    return <React.Fragment key={this.state.retryCount}>{this.props.children}</React.Fragment>;
  }
}

ReactDOM.createRoot(document.getElementById(rootElementId)!).render(
  <GlobalErrorBoundary>
    <QueryProvider>
      <App />
    </QueryProvider>
  </GlobalErrorBoundary>
);
