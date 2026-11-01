import { SearchView } from "./SearchView";

/** Files across every channel and chat the user can open, found by name or by the text inside them. */
export function FilesView() {
  return (
    <div style={{ display: "flex", flexDirection: "column", minHeight: 0, height: "100%" }}>
      <div style={{ padding: "24px 32px 0", maxWidth: 944, width: "100%", margin: "0 auto" }}>
        <h1 style={{ fontSize: "var(--m-text-display)", lineHeight: "var(--m-lh-display)", fontWeight: 600 }}>Files</h1>
      </div>
      <SearchView query="" initialScope="Files" embedded />
    </div>
  );
}
