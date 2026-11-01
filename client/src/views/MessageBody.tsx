import { memo } from "react";
import Markdown, { defaultUrlTransform, type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { useMode } from "../app/context";
import { renderableMarkdown } from "../lib/mentions";
import styles from "./views.module.css";

function Mention({ target }: { target: string }) {
  const me = useMode((s) => s.me?.id);
  const name = useMode((s) => s.users[target]?.displayName);
  if (target === "channel" || target === "workspace") return <span className={styles.mention}>@{target}</span>;
  return <span className={`${styles.mention} ${target === me ? styles.mentionMe : ""}`}>@{name ?? "Unknown user"}</span>;
}

const components: Components = {
  a({ href, children }) {
    if (href?.startsWith("mode-mention:")) return <Mention target={decodeURIComponent(href.slice("mode-mention:".length))} />;
    return <a href={href} target="_blank" rel="noopener noreferrer nofollow">{children}</a>;
  },
  // Images are refused by the server; never load remote images from message text.
  img: ({ alt }) => <span>{alt}</span>,
};

/** Only http(s), mailto and Mode's own mention links survive; everything else is dropped. */
function urlTransform(url: string): string {
  if (url.startsWith("mode-mention:")) return url;
  const safe = defaultUrlTransform(url);
  return /^(https?:|mailto:)/i.test(safe) ? safe : "";
}

/** Renders stored Markdown as React elements; raw HTML is never interpreted. */
export const MessageBody = memo(function MessageBody({ markdown }: { markdown: string }) {
  return (
    <div className={styles.body}>
      <Markdown remarkPlugins={[remarkGfm]} components={components} urlTransform={urlTransform} skipHtml>
        {renderableMarkdown(markdown)}
      </Markdown>
    </div>
  );
});
