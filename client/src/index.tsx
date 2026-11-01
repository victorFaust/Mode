import { useEffect, useState } from "react";
import { createRoot } from "react-dom/client";
import { createApi } from "./api/client";
import { AppShell } from "./app/AppShell";
import { ModeProvider } from "./app/context";
import { ModeController, type Platform } from "./store/controller";
import { createModeStore } from "./store/store";
import "./theme/tokens.css";

export interface ModeTheme {
  mode: "light" | "dark" | "system";
  /** Brand colour override; must keep 4.5:1 contrast with white text. Defaults to UBA red. */
  accent?: string;
}

export interface MountOptions {
  /** Returns a valid host access token; called before every request and on every realtime (re)connect. */
  getToken: () => Promise<string>;
  /** Where the Mode API lives, e.g. "https://mode.bank.example". Empty or omitted for the same origin. */
  apiBaseUrl?: string;
  theme?: ModeTheme;
  /** Called when the API rejects the token, so the host can sign the user in again. */
  onUnauthorized?: () => void;
  /** Service worker script for Web Push, served from the host's origin (see public/mode-sw.js). */
  serviceWorkerUrl?: string;
}

export interface ModeInstance {
  setTheme(theme: ModeTheme): void;
  unmount(): void;
}

function resolveTheme(mode: ModeTheme["mode"]): "light" | "dark" {
  if (mode !== "system") return mode;
  return window.matchMedia?.("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

function Root({ controller, theme }: { controller: ModeController; theme: ModeTheme }) {
  const [portal, setPortal] = useState<HTMLElement | null>(null);
  const [resolved, setResolved] = useState(() => resolveTheme(theme.mode));

  useEffect(() => {
    setResolved(resolveTheme(theme.mode));
    if (theme.mode !== "system" || !window.matchMedia) return;
    const query = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = () => setResolved(resolveTheme("system"));
    query.addEventListener("change", onChange);
    return () => query.removeEventListener("change", onChange);
  }, [theme.mode]);

  useEffect(() => {
    void controller.bootstrap();
    return () => void controller.stop();
  }, [controller]);

  return (
    <div data-mode-root data-theme={resolved} style={{ height: "100%", position: "relative", ...(theme.accent ? { ["--m-accent" as string]: theme.accent } : {}) }}>
      <ModeProvider value={{ controller, portal }}>
        <AppShell />
      </ModeProvider>
      <div ref={setPortal} />
    </div>
  );
}

/** Mounts Mode inside a host page element. The host owns sign-in; Mode never shows a login screen. */
export function mountMode(element: HTMLElement, options: MountOptions): ModeInstance {
  const api = createApi({ baseUrl: (options.apiBaseUrl ?? "").replace(/\/$/, ""), getToken: options.getToken, onUnauthorized: options.onUnauthorized });
  const platform: Platform = {
    serviceWorkerUrl: options.serviceWorkerUrl,
    notify(title, body, onClick) {
      if (!("Notification" in window) || Notification.permission !== "granted") return false;
      const notification = new Notification(title, { body, tag: "mode" });
      notification.onclick = () => {
        window.focus();
        onClick();
        notification.close();
      };
      return true;
    },
  };
  const controller = new ModeController(api, createModeStore(), options.getToken, platform);
  const root = createRoot(element);
  let theme: ModeTheme = options.theme ?? { mode: "light" };
  const render = () => root.render(<Root controller={controller} theme={theme} />);
  render();
  return {
    setTheme(next) {
      theme = next;
      render();
    },
    unmount() {
      root.unmount();
    },
  };
}
