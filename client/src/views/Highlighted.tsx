import { Fragment, type ReactNode } from "react";
import type { HighlightedText } from "../api/client";
import styles from "./views.module.css";

/** Search snippets come as plain text plus offsets; render them as text with <mark>, never as HTML. */
export function Highlighted({ value }: { value: HighlightedText }) {
  const parts: ReactNode[] = [];
  let position = 0;
  value.highlights.forEach((range, i) => {
    if (range.start > position) parts.push(<Fragment key={`t${i}`}>{value.text.slice(position, range.start)}</Fragment>);
    parts.push(<mark key={`m${i}`} className={styles.hit}>{value.text.slice(range.start, range.start + range.length)}</mark>);
    position = range.start + range.length;
  });
  if (position < value.text.length) parts.push(<Fragment key="end">{value.text.slice(position)}</Fragment>);
  return <>{parts}</>;
}
