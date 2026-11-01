import type { User } from "../api/client";

/** A person picked from the mention popover; the composer shows "@Name" and sends "@{id}". */
export interface PickedMention {
  id: string;
  name: string;
}

const tokenPattern = /@\{([^{}\s]{1,64})\}/g;

function escapeRegex(text: string): string {
  return text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** Converts the readable "@Name" text of picked mentions into the server's "@{id}" tokens. */
export function toWireMarkdown(text: string, picked: PickedMention[]): string {
  let result = text;
  for (const person of [...picked].sort((a, b) => b.name.length - a.name.length)) {
    result = result.replace(new RegExp(`@${escapeRegex(person.name)}(?![\\w])`, "g"), `@{${person.id}}`);
  }
  return result;
}

/** The reverse, for editing an existing message. */
export function toEditableText(markdown: string, users: Record<string, User>): { text: string; picked: PickedMention[] } {
  const picked: PickedMention[] = [];
  const text = markdown.replace(tokenPattern, (_, id: string) => {
    const name = users[id]?.displayName;
    if (!name) return `@{${id}}`;
    if (!picked.some((p) => p.id === id)) picked.push({ id, name });
    return `@${name}`;
  });
  return { text, picked };
}

/**
 * Prepares stored Markdown for display: mention tokens become links with a private scheme that the renderer
 * turns into mention chips. Code spans and blocks are left untouched, as the server does when parsing mentions.
 */
export function renderableMarkdown(markdown: string): string {
  return markdown
    .split(/(```[\s\S]*?```|`[^`\n]*`)/g)
    .map((part, i) => (i % 2 === 1 ? part : part
      .replace(tokenPattern, (_, id: string) => `[@${id}](mode-mention:${encodeURIComponent(id)})`)
      .replace(/(^|[^\w@])@(channel|workspace)\b/g, (_, lead: string, kind: string) => `${lead}[@${kind}](mode-mention:${kind})`)))
    .join("");
}

/** Finds an "@query" being typed just before the caret. */
export function activeMentionQuery(text: string, caret: number): { start: number; query: string } | null {
  const before = text.slice(0, caret);
  const match = /(^|\s)@([^\s@{}]{0,40})$/.exec(before);
  if (!match) return null;
  return { start: caret - match[2].length - 1, query: match[2] };
}
