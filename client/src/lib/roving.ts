import type { KeyboardEvent } from "react";

/** Arrow keys, Home and End move focus between the [data-roving] items inside the element. */
export function rovingKeyDown(e: KeyboardEvent<HTMLElement>): void {
  if (!["ArrowDown", "ArrowUp", "Home", "End"].includes(e.key)) return;
  const target = e.target as HTMLElement;
  if (target.isContentEditable || target.closest("input, textarea, select, [role=listbox]")) return;
  const items = Array.from(e.currentTarget.querySelectorAll<HTMLElement>("[data-roving]"));
  if (items.length === 0) return;
  const index = items.indexOf(document.activeElement as HTMLElement);
  const next = e.key === "Home" ? 0
    : e.key === "End" ? items.length - 1
    : e.key === "ArrowDown" ? Math.min(items.length - 1, index + 1)
    : Math.max(0, index - 1);
  e.preventDefault();
  items[next]?.focus();
}
