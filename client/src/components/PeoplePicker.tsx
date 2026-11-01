import { X } from "lucide-react";
import { useEffect, useId, useState } from "react";
import { ok, type User } from "../api/client";
import { useController, useMode } from "../app/context";
import { Avatar, IconButton, ui } from "./ui";
import viewStyles from "../views/views.module.css";

/** Directory search with selected people shown as removable chips. */
export function PeoplePicker({ selected, onChange, exclude = [], single = false, label = "People" }: {
  selected: User[]; onChange: (people: User[]) => void; exclude?: string[]; single?: boolean; label?: string;
}) {
  const controller = useController();
  const me = useMode((s) => s.me);
  const [query, setQuery] = useState("");
  const [results, setResults] = useState<User[]>([]);
  const [active, setActive] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const id = useId();
  const hidden = [...exclude, ...selected.map((s) => s.id)].join(",");

  useEffect(() => {
    const q = query.trim();
    if (q.length < 2) {
      setResults([]);
      return;
    }
    const skip = new Set(hidden.split(","));
    const handle = setTimeout(() => {
      void ok(controller.api.GET("/api/v1/users/search", { params: { query: { q, limit: 8 } } })).then((found) => {
        setError(null);
        setActive(0);
        setResults(found.filter((u) => u.id !== me?.id && !skip.has(u.id)));
      }, (e: Error) => setError(e.message));
    }, 200);
    return () => clearTimeout(handle);
  }, [query, controller, me?.id, hidden]);

  const pick = (user: User) => {
    onChange(single ? [user] : [...selected, user]);
    setQuery("");
    setResults([]);
  };

  return (
    <div className={ui.field}>
      <label className={ui.label} htmlFor={id}>{label}</label>
      {selected.length > 0 && (
        <div style={{ display: "flex", flexWrap: "wrap", gap: 6 }}>
          {selected.map((u) => (
            <span key={u.id} className={ui.chip} style={{ display: "inline-flex", alignItems: "center", gap: 6, cursor: "default" }}>
              {u.displayName}
              <IconButton label={`Remove ${u.displayName}`} size="small" tooltip={false} style={{ width: 20, height: 20 }}
                onClick={() => onChange(selected.filter((s) => s.id !== u.id))}><X size={12} aria-hidden /></IconButton>
            </span>
          ))}
        </div>
      )}
      {(!single || selected.length === 0) && (
        <input id={id} className={ui.input} value={query} autoComplete="off" placeholder="Type a name"
          role="combobox" aria-expanded={results.length > 0} aria-controls={`${id}-list`} aria-autocomplete="list"
          aria-activedescendant={results[active] ? `${id}-${results[active].id}` : undefined}
          onChange={(e) => setQuery(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "ArrowDown") { e.preventDefault(); setActive((a) => Math.min(results.length - 1, a + 1)); }
            if (e.key === "ArrowUp") { e.preventDefault(); setActive((a) => Math.max(0, a - 1)); }
            if (e.key === "Enter" && results[active]) { e.preventDefault(); pick(results[active]); }
          }} />
      )}
      {error && <span className={ui.error}>{error}</span>}
      {results.length > 0 && (
        <ul id={`${id}-list`} role="listbox" aria-label="Matching people" style={{ listStyle: "none", margin: 0, padding: 0 }}>
          {results.map((u, i) => (
            <li key={u.id} id={`${id}-${u.id}`} role="option" aria-selected={i === active} className={viewStyles.option}
              onMouseDown={(e) => { e.preventDefault(); pick(u); }} onMouseEnter={() => setActive(i)}>
              <Avatar id={u.id} name={u.displayName} avatarUrl={u.avatarUrl} size={24} />
              {u.displayName}
              <span className={viewStyles.optionMeta}>{u.email}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
