import { Download, RotateCw, Shield } from "lucide-react";
import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from "react";
import {
  ok, type AdminWorkspace, type AuditEntry, type ComplianceExport, type DlpPattern, type HoldTarget, type LegalHold, type Webhook,
  type WebhookSecret,
} from "../api/client";
import { useController, useMode } from "../app/context";
import { Button, Dialog, DialogActions, EmptyState, TextField } from "../components/ui";
import { formatFull } from "../lib/format";
import styles from "./admin.module.css";

type Tab = "workspaces" | "audit" | "holds" | "dlp" | "webhooks" | "exports";

const tabs: { id: Tab; label: string }[] = [
  { id: "workspaces", label: "Workspaces" },
  { id: "audit", label: "Audit log" },
  { id: "holds", label: "Legal holds" },
  { id: "exports", label: "Exports" },
  { id: "webhooks", label: "Webhooks" },
  { id: "dlp", label: "Data loss" },
];

/** Governance for Mode administrators (host admin role). Accounts themselves are managed in the host application. */
export function AdminView() {
  const isAdmin = useMode((s) => s.isAdmin);
  const [tab, setTab] = useState<Tab>("workspaces");
  if (!isAdmin) return <EmptyState icon={<Shield />} title="Administrators only">Ask a Mode administrator for help.</EmptyState>;

  return (
    <div className={styles.page}>
      <h1 className={styles.title}>Admin</h1>
      <div role="tablist" aria-label="Admin sections" className={styles.tabs}>
        {tabs.map((t) => (
          <button key={t.id} type="button" role="tab" id={`admin-tab-${t.id}`} aria-selected={tab === t.id} aria-controls="admin-panel"
            className={styles.tab} onClick={() => setTab(t.id)}>{t.label}</button>
        ))}
      </div>
      <section role="tabpanel" id="admin-panel" aria-labelledby={`admin-tab-${tab}`} className={styles.panel}>
        {tab === "workspaces" && <WorkspacesTab />}
        {tab === "audit" && <AuditTab />}
        {tab === "holds" && <HoldsTab />}
        {tab === "exports" && <ExportsTab />}
        {tab === "webhooks" && <WebhooksTab />}
        {tab === "dlp" && <DlpTab />}
      </section>
    </div>
  );
}

function useLoad<T>(load: () => Promise<T>): { data: T | null; error: string | null; reload: () => void } {
  const [data, setData] = useState<T | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [version, setVersion] = useState(0);
  useEffect(() => {
    let cancelled = false;
    load().then((d) => !cancelled && (setData(d), setError(null)), (e: Error) => !cancelled && setError(e.message));
    return () => { cancelled = true; };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [version]);
  return { data, error, reload: useCallback(() => setVersion((v) => v + 1), []) };
}

function Status({ error, children }: { error: string | null; children?: ReactNode }) {
  if (error) return <p role="alert" className={styles.error}>{error}</p>;
  return children ? <p className={styles.muted}>{children}</p> : null;
}

function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.click();
  URL.revokeObjectURL(url);
}

// ---- Workspaces and retention --------------------------------------------------------------------

function WorkspacesTab() {
  const controller = useController();
  const { data, error, reload } = useLoad(() => ok(controller.api.GET("/api/v1/admin/workspaces")));
  if (!data) return <Status error={error}>Loading…</Status>;
  if (data.length === 0) return <p className={styles.muted}>No workspaces yet.</p>;
  return (
    <table className={styles.table}>
      <caption className="m-visually-hidden">Workspaces</caption>
      <thead><tr><th>Name</th><th>Members</th><th>Channels</th><th>Keep channel messages</th></tr></thead>
      <tbody>{data.map((w) => <WorkspaceRow key={w.id} workspace={w} onSaved={reload} />)}</tbody>
    </table>
  );
}

function WorkspaceRow({ workspace, onSaved }: { workspace: AdminWorkspace; onSaved: () => void }) {
  const controller = useController();
  const [days, setDays] = useState(workspace.retentionDays?.toString() ?? "");
  const [error, setError] = useState<string | null>(null);
  const save = async (e: FormEvent) => {
    e.preventDefault();
    const keepDays = days.trim() === "" ? null : Number(days);
    try {
      await ok(controller.api.PUT("/api/v1/admin/workspaces/{workspaceId}/retention", {
        params: { path: { workspaceId: workspace.id } }, body: { keepDays },
      }));
      setError(null);
      onSaved();
    } catch (err) {
      setError((err as Error).message);
    }
  };
  return (
    <tr>
      <td>{workspace.name}</td>
      <td>{workspace.memberCount}</td>
      <td>{workspace.channelCount}</td>
      <td>
        <form className={styles.inline} onSubmit={(e) => void save(e)}>
          <label className="m-visually-hidden" htmlFor={`keep-${workspace.id}`}>Days to keep messages in {workspace.name}</label>
          <input id={`keep-${workspace.id}`} className={styles.number} inputMode="numeric" placeholder="Forever" value={days}
            onChange={(e) => setDays(e.target.value.replace(/\D/g, ""))} />
          <span>days</span>
          <Button type="submit">Save</Button>
        </form>
        {error && <span role="alert" className={styles.error}>{error}</span>}
      </td>
    </tr>
  );
}

// ---- Audit log -------------------------------------------------------------------------------------

function AuditTab() {
  const controller = useController();
  const [filters, setFilters] = useState({ actor: "", action: "", from: "", to: "" });
  const [items, setItems] = useState<AuditEntry[]>([]);
  const [next, setNext] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);

  const query = () => ({
    actor: filters.actor.trim() || undefined,
    action: filters.action.trim() || undefined,
    from: filters.from ? new Date(filters.from).toISOString() : undefined,
    to: filters.to ? new Date(`${filters.to}T23:59:59.999`).toISOString() : undefined,
  });

  const load = async (before?: number) => {
    try {
      const page = await ok(controller.api.GET("/api/v1/admin/audit", { params: { query: { ...query(), before, limit: 50 } } }));
      setItems((prev) => (before ? [...prev, ...page.items] : page.items));
      setNext(page.nextBefore ?? null);
      setError(null);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setLoaded(true);
    }
  };

  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => void load(), []);

  const exportCsv = async () => {
    try {
      const blob = await ok(controller.api.GET("/api/v1/admin/audit/export", { params: { query: query() }, parseAs: "blob" }));
      saveBlob(blob as Blob, `mode-audit-${new Date().toISOString().slice(0, 10)}.csv`);
    } catch (e) {
      setError((e as Error).message);
    }
  };

  return (
    <>
      <form className={styles.filters} onSubmit={(e) => { e.preventDefault(); void load(); }}>
        <TextField label="Person (user id)" value={filters.actor} onChange={(actor) => setFilters({ ...filters, actor })} />
        <TextField label="Action starts with" placeholder="e.g. hold." value={filters.action} onChange={(action) => setFilters({ ...filters, action })} />
        <DateField label="From" value={filters.from} onChange={(from) => setFilters({ ...filters, from })} />
        <DateField label="To" value={filters.to} onChange={(to) => setFilters({ ...filters, to })} />
        <div className={styles.inline}>
          <Button type="submit" variant="primary">Filter</Button>
          <Button type="button" onClick={() => void exportCsv()}><Download size={16} aria-hidden /> Export CSV</Button>
        </div>
      </form>
      <Status error={error} />
      {loaded && items.length === 0 && !error && <p className={styles.muted}>No entries match.</p>}
      {items.length > 0 && (
        <table className={styles.table}>
          <caption className="m-visually-hidden">Audit log</caption>
          <thead><tr><th>When</th><th>Who</th><th>Action</th><th>Target</th><th>Details</th></tr></thead>
          <tbody>
            {items.map((a) => (
              <tr key={a.id}>
                <td><time dateTime={a.at}>{formatFull(a.at)}</time></td>
                <td>{a.actorName ?? "System"}</td>
                <td><code>{a.action}</code></td>
                <td>{a.targetType}{a.targetId ? ` ${a.targetId}` : ""}</td>
                <td className={styles.details}>{a.data ? JSON.stringify(a.data) : ""}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {next !== null && <Button onClick={() => void load(next)}>Load more</Button>}
    </>
  );
}

function DateField({ label, value, onChange }: { label: string; value: string; onChange: (value: string) => void }) {
  const id = `date-${label.toLowerCase()}`;
  return (
    <div className={styles.field}>
      <label htmlFor={id}>{label}</label>
      <input id={id} type="date" className={styles.date} value={value} onChange={(e) => onChange(e.target.value)} />
    </div>
  );
}

// ---- Legal holds -----------------------------------------------------------------------------------

const holdTargets: { value: HoldTarget; label: string; hint: string }[] = [
  { value: "User", label: "Person", hint: "Their user id from the host application." },
  { value: "Conversation", label: "Chat", hint: "The chat id (from a message link)." },
  { value: "Channel", label: "Channel", hint: "The channel id." },
];

function HoldsTab() {
  const controller = useController();
  const { data, error, reload } = useLoad(() => ok(controller.api.GET("/api/v1/admin/holds")));
  const [targetType, setTargetType] = useState<HoldTarget>("User");
  const [targetId, setTargetId] = useState("");
  const [reason, setReason] = useState("");
  const [formError, setFormError] = useState<string | null>(null);

  const place = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await ok(controller.api.POST("/api/v1/admin/holds", { body: { targetType, targetId: targetId.trim(), reason } }));
      setTargetId("");
      setReason("");
      setFormError(null);
      reload();
    } catch (err) {
      setFormError((err as Error).message);
    }
  };
  const release = (hold: LegalHold) =>
    void ok(controller.api.POST("/api/v1/admin/holds/{holdId}/release", { params: { path: { holdId: hold.id } } }))
      .then(reload, (e: Error) => controller.toastError(e.message));

  return (
    <>
      <p className={styles.muted}>Held content is never removed by retention, even when the policy would delete it.</p>
      <form className={styles.filters} onSubmit={(e) => void place(e)}>
        <div className={styles.field}>
          <label htmlFor="hold-type">Hold</label>
          <select id="hold-type" className={styles.select} value={targetType} onChange={(e) => setTargetType(e.target.value as HoldTarget)}>
            {holdTargets.map((t) => <option key={t.value} value={t.value}>{t.label}</option>)}
          </select>
        </div>
        <TextField label="Id" hint={holdTargets.find((t) => t.value === targetType)!.hint} value={targetId} onChange={setTargetId} />
        <TextField label="Reason" maxLength={500} value={reason} onChange={setReason} />
        <div className={styles.inline}><Button type="submit" variant="primary" disabled={!targetId.trim() || !reason.trim()}>Place hold</Button></div>
      </form>
      <Status error={formError ?? error} />
      {data && data.length === 0 && <p className={styles.muted}>No holds.</p>}
      {data && data.length > 0 && (
        <table className={styles.table}>
          <caption className="m-visually-hidden">Legal holds</caption>
          <thead><tr><th>On</th><th>Reason</th><th>Placed</th><th>Status</th><th /></tr></thead>
          <tbody>
            {data.map((h) => (
              <tr key={h.id}>
                <td>{h.targetName} <span className={styles.muted}>({h.targetType})</span></td>
                <td>{h.reason}</td>
                <td>{formatFull(h.createdAt)} by {h.createdBy.displayName}</td>
                <td>{h.releasedAt ? `Released ${formatFull(h.releasedAt)}` : "Active"}</td>
                <td>{!h.releasedAt && <Button onClick={() => release(h)}>Release</Button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}

// ---- Compliance exports ----------------------------------------------------------------------------

function ExportsTab() {
  const controller = useController();
  const { data, error, reload } = useLoad(() => ok(controller.api.GET("/api/v1/admin/exports")));
  const [form, setForm] = useState({ userId: "", conversationId: "", from: "", to: "" });
  const [formError, setFormError] = useState<string | null>(null);

  const pending = data?.some((e) => e.status === "Pending") ?? false;
  useEffect(() => {
    if (!pending) return;
    const timer = window.setInterval(reload, 5000);
    return () => window.clearInterval(timer);
  }, [pending, reload]);

  const request = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await ok(controller.api.POST("/api/v1/admin/exports", {
        body: {
          userId: form.userId.trim() || null,
          conversationId: form.conversationId.trim() || null,
          from: form.from ? new Date(form.from).toISOString() : null,
          to: form.to ? new Date(`${form.to}T23:59:59.999`).toISOString() : null,
        },
      }));
      setFormError(null);
      reload();
    } catch (err) {
      setFormError((err as Error).message);
    }
  };

  const download = (x: ComplianceExport) =>
    void ok(controller.api.GET("/api/v1/admin/exports/{exportId}/download", { params: { path: { exportId: x.id } }, parseAs: "blob" }))
      .then((blob) => saveBlob(blob as Blob, `mode-export-${x.id.slice(0, 8)}.zip`), (e: Error) => controller.toastError(e.message));

  return (
    <>
      <p className={styles.muted}>
        A ZIP of messages (with earlier versions), files and a signed manifest. Downloads are recorded in the audit log.
      </p>
      <form className={styles.filters} onSubmit={(e) => void request(e)}>
        <TextField label="Person (user id)" value={form.userId} onChange={(userId) => setForm({ ...form, userId })} />
        <TextField label="Chat id" value={form.conversationId} onChange={(conversationId) => setForm({ ...form, conversationId })} />
        <DateField label="From" value={form.from} onChange={(from) => setForm({ ...form, from })} />
        <DateField label="To" value={form.to} onChange={(to) => setForm({ ...form, to })} />
        <div className={styles.inline}><Button type="submit" variant="primary">Request export</Button></div>
      </form>
      <Status error={formError ?? error} />
      {data && data.length > 0 && (
        <table className={styles.table}>
          <caption className="m-visually-hidden">Exports</caption>
          <thead><tr><th>Requested</th><th>Status</th><th>Contents</th><th>Manifest SHA-256</th><th /></tr></thead>
          <tbody>
            {data.map((x) => (
              <tr key={x.id}>
                <td>{formatFull(x.createdAt)} by {x.requestedBy.displayName}</td>
                <td>{x.status === "Failed" ? `Failed: ${x.error ?? ""}` : x.status}</td>
                <td>{x.status === "Ready" ? `${x.messageCount} messages, ${x.fileCount} files` : ""}</td>
                <td className={styles.details}>{x.manifestSha256 ?? ""}</td>
                <td>{x.status === "Ready" && <Button onClick={() => download(x)}><Download size={16} aria-hidden /> Download</Button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  );
}

// ---- Incoming webhooks -----------------------------------------------------------------------------

function WebhooksTab() {
  const controller = useController();
  const groups = useMode((s) => s.conversations.filter((c) => c.type === "Group"));
  const { data, error, reload } = useLoad(() => ok(controller.api.GET("/api/v1/admin/webhooks")));
  const [name, setName] = useState("");
  const [conversationId, setConversationId] = useState("");
  const [formError, setFormError] = useState<string | null>(null);
  const [secret, setSecret] = useState<WebhookSecret | null>(null);

  const create = async (e: FormEvent) => {
    e.preventDefault();
    try {
      setSecret(await ok(controller.api.POST("/api/v1/admin/webhooks", { body: { conversationId, name } })));
      setName("");
      setFormError(null);
      reload();
    } catch (err) {
      setFormError((err as Error).message);
    }
  };
  const rotate = (hook: Webhook) =>
    void ok(controller.api.POST("/api/v1/admin/webhooks/{webhookId}/rotate", { params: { path: { webhookId: hook.id } } }))
      .then((s) => { setSecret(s); reload(); }, (e: Error) => controller.toastError(e.message));
  const revoke = (hook: Webhook) =>
    void ok(controller.api.DELETE("/api/v1/admin/webhooks/{webhookId}", { params: { path: { webhookId: hook.id } } }))
      .then(reload, (e: Error) => controller.toastError(e.message));

  return (
    <>
      <p className={styles.muted}>Other systems post into a group chat you belong to. Messages are labelled as a bot.</p>
      <form className={styles.filters} onSubmit={(e) => void create(e)}>
        <TextField label="Bot name" maxLength={80} value={name} onChange={setName} />
        <div className={styles.field}>
          <label htmlFor="hook-chat">Group chat</label>
          <select id="hook-chat" className={styles.select} value={conversationId} onChange={(e) => setConversationId(e.target.value)}>
            <option value="">Choose…</option>
            {groups.map((c) => <option key={c.id} value={c.id}>{c.name ?? c.members.map((m) => m.displayName).join(", ")}</option>)}
          </select>
        </div>
        <div className={styles.inline}><Button type="submit" variant="primary" disabled={!name.trim() || !conversationId}>Create webhook</Button></div>
      </form>
      <Status error={formError ?? error} />
      {data && data.length > 0 && (
        <table className={styles.table}>
          <caption className="m-visually-hidden">Webhooks</caption>
          <thead><tr><th>Name</th><th>Posts to</th><th>Last used</th><th>Status</th><th /></tr></thead>
          <tbody>
            {data.map((h) => (
              <tr key={h.id}>
                <td>{h.name}</td>
                <td>{h.roomName}</td>
                <td>{h.lastUsedAt ? formatFull(h.lastUsedAt) : "Never"}</td>
                <td>{h.revokedAt ? "Revoked" : "Active"}</td>
                <td className={styles.inline}>
                  {!h.revokedAt && <Button onClick={() => rotate(h)}><RotateCw size={16} aria-hidden /> New key</Button>}
                  {!h.revokedAt && <Button onClick={() => revoke(h)}>Revoke</Button>}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      <Dialog open={secret !== null} onOpenChange={(open) => !open && setSecret(null)} title="Copy the key now"
        description="Mode only stores a hash of it, so it can't be shown again.">
        {secret && (
          <dl className={styles.secret}>
            <dt>URL</dt><dd><code>{new URL(secret.webhook.url, window.location.origin).toString()}</code></dd>
            <dt>Header</dt><dd><code>{secret.header}: {secret.secret}</code></dd>
            <dt>Body</dt><dd><code>{"{\"text\": \"Build passed\"}"}</code></dd>
          </dl>
        )}
        <DialogActions>
          <Button onClick={() => void navigator.clipboard?.writeText(secret?.secret ?? "")}>Copy key</Button>
          <Button variant="primary" onClick={() => setSecret(null)}>Done</Button>
        </DialogActions>
      </Dialog>
    </>
  );
}

// ---- Data-loss controls ----------------------------------------------------------------------------

function DlpTab() {
  const controller = useController();
  const { data, error } = useLoad(() => ok(controller.api.GET("/api/v1/admin/dlp")));
  const [extensions, setExtensions] = useState("");
  const [patterns, setPatterns] = useState<DlpPattern[]>([]);
  const [saveError, setSaveError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => {
    if (!data) return;
    setExtensions(data.blockedExtensions.join(", "));
    setPatterns(data.patterns);
  }, [data]);

  const update = (index: number, change: Partial<DlpPattern>) =>
    setPatterns((list) => list.map((p, i) => (i === index ? { ...p, ...change } : p)));

  const save = async (e: FormEvent) => {
    e.preventDefault();
    try {
      await ok(controller.api.PUT("/api/v1/admin/dlp", {
        body: { blockedExtensions: extensions.split(/[\s,]+/).filter(Boolean), patterns },
      }));
      setSaveError(null);
      setSaved(true);
    } catch (err) {
      setSaveError((err as Error).message);
      setSaved(false);
    }
  };

  if (!data) return <Status error={error}>Loading…</Status>;
  return (
    <form onSubmit={(e) => void save(e)} className={styles.stack}>
      <TextField label="Blocked file types" hint="Comma-separated, e.g. exe, bat" value={extensions}
        onChange={(v) => { setExtensions(v); setSaved(false); }} />
      <fieldset className={styles.fieldset}>
        <legend>Message patterns</legend>
        <p className={styles.muted}>Warn lets the sender confirm; Block stops the message. Patterns are regular expressions.</p>
        {patterns.map((p, i) => (
          <div key={i} className={styles.patternRow}>
            <TextField label="Name" value={p.name} maxLength={80} onChange={(name) => update(i, { name })} />
            <TextField label="Pattern" value={p.pattern} maxLength={500} onChange={(pattern) => update(i, { pattern })} />
            <div className={styles.field}>
              <label htmlFor={`dlp-action-${i}`}>Action</label>
              <select id={`dlp-action-${i}`} className={styles.select} value={p.action}
                onChange={(e) => update(i, { action: e.target.value as DlpPattern["action"] })}>
                <option value="Warn">Warn</option>
                <option value="Block">Block</option>
              </select>
            </div>
            <Button type="button" onClick={() => setPatterns((list) => list.filter((_, j) => j !== i))}>Remove</Button>
          </div>
        ))}
        <Button type="button" onClick={() => setPatterns((list) => [...list, { name: "", pattern: "", action: "Warn" }])}>Add pattern</Button>
      </fieldset>
      <div className={styles.inline}>
        <Button type="submit" variant="primary">Save</Button>
        {saved && <span role="status">Saved.</span>}
      </div>
      <Status error={saveError} />
    </form>
  );
}
