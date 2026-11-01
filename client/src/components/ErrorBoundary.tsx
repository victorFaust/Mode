import { AlertTriangle } from "lucide-react";
import { Component, type ReactNode } from "react";
import { Button, EmptyState } from "./ui";

/** Keeps one broken pane from taking down the whole app. */
export class ErrorBoundary extends Component<{ children: ReactNode; resetKey?: string }, { error: Error | null }> {
  state = { error: null as Error | null };

  static getDerivedStateFromError(error: Error) {
    return { error };
  }

  componentDidUpdate(previous: { resetKey?: string }) {
    if (previous.resetKey !== this.props.resetKey && this.state.error) this.setState({ error: null });
  }

  render() {
    if (!this.state.error) return this.props.children;
    return (
      <EmptyState icon={<AlertTriangle />} title="This view ran into a problem"
        action={<Button onClick={() => this.setState({ error: null })}>Try again</Button>}>
        {this.state.error.message}
      </EmptyState>
    );
  }
}
