import { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import { mountMode, type ModeInstance, type ModeTheme } from "../index";
import "./dev.css";

/** Seeded in the mock host directory by db/host-mock/setup.sql. Development only. */
const people = [
  { id: "0a1b0001-0000-4000-8000-000000000001", name: "Ada Okafor" },
  { id: "0a1b0001-0000-4000-8000-000000000002", name: "Bola Adeyemi" },
  { id: "0a1b0001-0000-4000-8000-000000000003", name: "Chidi Nwosu" },
  { id: "0a1b0001-0000-4000-8000-000000000004", name: "Deborah Eze" },
  { id: "0a1b0001-0000-4000-8000-000000000005", name: "Emeka Obi" },
  { id: "0a1b0001-0000-4000-8000-000000000006", name: "Funke Bello" },
  { id: "0a1b0001-0000-4000-8000-000000000007", name: "Grace Johnson" },
  { id: "0a1b0001-0000-4000-8000-000000000008", name: "Hassan Musa" },
  { id: "0a1b0001-0000-4000-8000-000000000010", name: "Jide Lawal" },
];

interface Session {
  sub: string;
  name: string;
  guest: boolean;
  admin?: boolean;
}

interface CachedToken {
  token: string;
  expiresAt: number;
}

const sessionKey = "mode.dev.session";

/** Fetches tokens from the mock issuer (proxied at /issuer) and renews them before they expire. */
function tokenSource(session: Session) {
  let cached: CachedToken | null = null;
  let pending: Promise<string> | null = null;
  return async (): Promise<string> => {
    if (cached && cached.expiresAt - Date.now() > 120_000) return cached.token;
    pending ??= fetch("/issuer/token", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ sub: session.sub, name: session.name, roles: session.guest ? ["guest"] : session.admin ? ["mode-admin"] : [] }),
    }).then(async (response) => {
      if (!response.ok) throw new Error(`Mock issuer returned ${response.status}. Is it running on port 5100?`);
      const body = (await response.json()) as { access_token: string; expires_at: string };
      cached = { token: body.access_token, expiresAt: Date.parse(body.expires_at) };
      return body.access_token;
    }).finally(() => { pending = null; });
    return pending;
  };
}

function DevShell() {
  const [session, setSession] = useState<Session | null>(() => JSON.parse(sessionStorage.getItem(sessionKey) ?? "null"));
  const [theme, setTheme] = useState<ModeTheme["mode"]>(() => (localStorage.getItem("mode.dev.theme") as ModeTheme["mode"]) ?? "light");
  const [chosen, setChosen] = useState(people[0].id);
  const [guest, setGuest] = useState(false);
  const [admin, setAdmin] = useState(false);
  const host = useRef<HTMLDivElement>(null);
  const instance = useRef<ModeInstance | null>(null);

  useEffect(() => {
    if (!session || !host.current) return;
    instance.current = mountMode(host.current, {
      getToken: tokenSource(session),
      theme: { mode: theme },
      serviceWorkerUrl: "/mode-sw.js",
      onUnauthorized: () => console.warn("Mode API returned 401"),
    });
    return () => {
      instance.current?.unmount();
      instance.current = null;
    };
    // The theme is applied separately so switching it doesn't remount Mode.
  }, [session]);

  useEffect(() => {
    localStorage.setItem("mode.dev.theme", theme);
    instance.current?.setTheme({ mode: theme });
    document.documentElement.dataset.devTheme = theme;
  }, [theme]);

  if (!session) {
    return (
      <main className="dev-signin">
        <form onSubmit={(e) => {
          e.preventDefault();
          const person = people.find((p) => p.id === chosen)!;
          const next = { sub: person.id, name: person.name, guest, admin: admin && !guest };
          sessionStorage.setItem(sessionKey, JSON.stringify(next));
          setSession(next);
        }}>
          <h1>Mode dev shell</h1>
          <p>Stands in for the host application. Pick a seeded user; a token is issued by the local mock issuer.</p>
          <label htmlFor="dev-user">Sign in as</label>
          <select id="dev-user" value={chosen} onChange={(e) => setChosen(e.target.value)}>
            {people.map((p) => <option key={p.id} value={p.id}>{p.name}</option>)}
          </select>
          <label className="dev-check"><input type="checkbox" checked={guest} onChange={(e) => setGuest(e.target.checked)} /> Host guest</label>
          <label className="dev-check"><input type="checkbox" checked={admin} onChange={(e) => setAdmin(e.target.checked)} /> Mode admin</label>
          <button type="submit">Sign in</button>
        </form>
      </main>
    );
  }

  return (
    <div className="dev-host">
      <header className="dev-bar">
        <strong>Host app</strong>
        <span>Signed in as {session.name}{session.guest ? " (guest)" : session.admin ? " (admin)" : ""}</span>
        <span className="dev-spacer" />
        <label>Theme{" "}
          <select value={theme} onChange={(e) => setTheme(e.target.value as ModeTheme["mode"])}>
            <option value="light">Light</option>
            <option value="dark">Dark</option>
            <option value="system">System</option>
          </select>
        </label>
        <button type="button" onClick={() => { sessionStorage.removeItem(sessionKey); setSession(null); }}>Sign out</button>
      </header>
      <div ref={host} className="dev-mount" />
    </div>
  );
}

createRoot(document.getElementById("root")!).render(<DevShell />);
