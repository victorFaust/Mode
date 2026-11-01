import { ArrowLeft, FileText, Hash, MessageSquare, Search as SearchIcon, X } from "lucide-react";
import { useEffect, useId, useState } from "react";
import { ok, type SearchResponse, type Schemas } from "../api/client";
import { useController, useMode } from "../app/context";
import { features } from "../app/features";
import { MessageSkeleton } from "../components/ShellSkeleton";
import { Avatar, Button, EmptyState, IconButton, ui } from "../components/ui";
import { formatBytes, formatListTime } from "../lib/format";
import { AttachmentCard } from "./AttachmentCard";
import { Highlighted } from "./Highlighted";
import styles from "./views.module.css";

type Scope = Schemas["SearchScope"];

const scopes: { value: Scope; label: string }[] = [
  { value: "All", label: "All" },
  { value: "Messages", label: "Messages" },
  { value: "Files", label: "Files" },
  { value: "People", label: "People" },
];

/** Full-text search over messages, files and people; only things the user can open are returned. */
export function SearchView({ query, initialScope = "All", embedded = false }: { query: string; initialScope?: Scope; embedded?: boolean }) {
  const controller = useController();
  const isGuest = useMode((s) => s.isGuest);
  const [input, setInput] = useState(query);
  const [scope, setScope] = useState<Scope>(initialScope);
  const [result, setResult] = useState<SearchResponse | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [submitted, setSubmitted] = useState(query);
  const [attempt, setAttempt] = useState(0);
  const id = useId();

  useEffect(() => {
    setInput(query);
    setSubmitted(query);
  }, [query]);

  useEffect(() => {
    if (submitted.trim().length < 2) {
      setResult(null);
      return;
    }
    let cancelled = false;
    setResult(null);
    setError(null);
    void ok(controller.api.GET("/api/v1/search", { params: { query: { q: submitted.trim(), scope, limit: 20 } } })).then(
      (r) => !cancelled && setResult(features.channels ? r : {
        ...r,
        messages: r.messages.filter((m) => m.room.type !== "Channel"),
        files: r.files.filter((f) => f.room.type !== "Channel"),
      }),
      (e: Error) => !cancelled && setError(e.message));
    return () => { cancelled = true; };
  }, [submitted, scope, controller, attempt]);

  const empty = result && result.messages.length + result.files.length + result.people.length === 0;
  return (
    <div className={styles.page}>
      <div className={styles.pageInner}>
        {!embedded && (
          <div style={{ display: "flex", alignItems: "center", gap: 8 }}>
            <IconButton label="Close search" onClick={() => controller.search("")}><ArrowLeft size={18} aria-hidden /></IconButton>
            <h1 className={styles.pageTitle}>Search</h1>
          </div>
        )}
        <form className={styles.searchBox} role="search" onSubmit={(e) => { e.preventDefault(); setSubmitted(input); setAttempt((a) => a + 1); }}>
          <label htmlFor={id} className="m-visually-hidden">Search</label>
          <input id={id} className={ui.input} value={input} onChange={(e) => setInput(e.target.value)} autoFocus
            placeholder={embedded ? "Find files by name or content" : "Search messages, files and people"} />
          {input && <IconButton label="Clear" type="button" onClick={() => { setInput(""); setSubmitted(""); }}><X size={16} aria-hidden /></IconButton>}
          <Button variant="primary" type="submit"><SearchIcon size={16} aria-hidden /> Search</Button>
        </form>
        {!embedded && (
          <div className={ui.segmented} role="group" aria-label="Search in">
            {scopes.filter((s) => !(isGuest && s.value === "People")).map((s) => (
              <button key={s.value} type="button" className={ui.chip} aria-pressed={scope === s.value} onClick={() => setScope(s.value)}>{s.label}</button>
            ))}
          </div>
        )}

        {submitted.trim().length < 2 ? (
          <EmptyState icon={<SearchIcon />} title="Search Mode">Use quotes for exact phrases, OR between alternatives, and -word to exclude.</EmptyState>
        ) : error ? (
          <p role="alert" className={ui.error}>{error}</p>
        ) : !result ? (
          <div style={{ display: "grid", gap: 24 }} aria-busy="true"><MessageSkeleton /><MessageSkeleton /></div>
        ) : empty ? (
          <EmptyState icon={<SearchIcon />} title={`No results for “${submitted.trim()}”`}>Try fewer or different words.</EmptyState>
        ) : (
          <div aria-live="polite" style={{ display: "grid", gap: 24 }}>
            {result.messages.length > 0 && (
              <section className={styles.section} aria-labelledby={`${id}-m`}>
                <h2 id={`${id}-m`} className={styles.sectionTitle}>Messages</h2>
                {result.messages.map((hit) => (
                  <button key={hit.messageId} type="button" className={styles.result}
                    onClick={() => { controller.search(""); void controller.revealMessage(hit.room, hit.messageId); }}>
                    <span className={styles.resultMeta}>
                      <Avatar id={hit.author.id} name={hit.author.displayName} size={20} />
                      <strong>{hit.author.displayName}</strong>
                      {hit.room.type === "Channel" ? <Hash size={14} aria-hidden /> : <MessageSquare size={14} aria-hidden />}
                      {hit.roomName}
                      <span>· {formatListTime(hit.createdAt)}</span>
                      {hit.parentMessageId && <span>· in a thread</span>}
                    </span>
                    <span><Highlighted value={hit.snippet} /></span>
                  </button>
                ))}
              </section>
            )}
            {result.files.length > 0 && (
              <section className={styles.section} aria-labelledby={`${id}-f`}>
                <h2 id={`${id}-f`} className={styles.sectionTitle}>Files</h2>
                {result.files.map((hit) => (
                  <div key={hit.fileId} className={styles.result} style={{ cursor: "default" }}>
                    <span className={styles.resultMeta}>
                      <FileText size={14} aria-hidden />
                      <strong>{hit.name}</strong>
                      <span>{hit.roomName}</span>
                      <span>· {hit.uploadedBy.displayName}</span>
                      <span>· {formatListTime(hit.uploadedAt)}</span>
                      <span>· {formatBytes(hit.sizeBytes)}</span>
                    </span>
                    {hit.snippet && <span><Highlighted value={hit.snippet} /></span>}
                    <AttachmentCard attachment={{
                      fileId: hit.fileId, name: hit.name, sizeBytes: hit.sizeBytes, contentType: hit.contentType,
                      scanStatus: "Clean", previewStatus: "NotSupported",
                    }} />
                  </div>
                ))}
              </section>
            )}
            {result.people.length > 0 && (
              <section className={styles.section} aria-labelledby={`${id}-p`}>
                <h2 id={`${id}-p`} className={styles.sectionTitle}>People</h2>
                <div className={styles.peopleGrid}>
                  {result.people.map((person) => (
                    <button key={person.id} type="button" className={styles.person}
                      onClick={() => void controller.startConversation([person.id], null)}>
                      <Avatar id={person.id} name={person.displayName} avatarUrl={person.avatarUrl} size={32} />
                      <span style={{ display: "grid", minWidth: 0 }}>
                        <span>{person.displayName}</span>
                        <span className={styles.roomPreview}>{person.email}</span>
                      </span>
                    </button>
                  ))}
                </div>
              </section>
            )}
          </div>
        )}
      </div>
    </div>
  );
}
