import * as RadixTooltip from "@radix-ui/react-tooltip";
import { AlertTriangle } from "lucide-react";
import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import { useShallow } from "zustand/react/shallow";
import { ActivityList } from "../views/ActivityList";
import { AdminView } from "../views/AdminView";
import { ChannelsList } from "../views/ChannelsList";
import { ChatsList } from "../views/ChatsList";
import { FilesView } from "../views/FilesView";
import { RoomPane } from "../views/RoomPane";
import { SearchView } from "../views/SearchView";
import { SidePanel } from "../views/SidePanel";
import { WelcomePane } from "../views/WelcomePane";
import { Button, EmptyState } from "../components/ui";
import { ConnectionBanner } from "../components/ConnectionBanner";
import { CommandBar } from "../components/CommandBar";
import { DlpWarningDialog } from "../components/DlpWarningDialog";
import { ErrorBoundary } from "../components/ErrorBoundary";
import { Toasts } from "../components/Toasts";
import { ShellSkeleton } from "../components/ShellSkeleton";
import { NavRail } from "./NavRail";
import { useController, useMode } from "./context";
import styles from "./AppShell.module.css";

const MIN_LIST = 220;
const MAX_LIST = 360;
const tabletQuery = "(max-width: 1024px)";

export function AppShell() {
  const controller = useController();
  const { bootstrapped, bootstrapError, section, room, side, searchQuery, listOpen } = useMode(useShallow((s) => ({
    bootstrapped: s.bootstrapped, bootstrapError: s.bootstrapError, section: s.section, room: s.room, side: s.side,
    searchQuery: s.searchQuery, listOpen: s.listOpen,
  })));
  const [listWidth, setListWidth] = useState(() => Number(localStorage.getItem("mode.listWidth")) || 280);
  const shellRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === "k") {
        e.preventDefault();
        controller.openCommandBar(true);
      }
    };
    const onActivity = () => controller.realtime.noteActivity();
    const onOnline = () => controller.setOnline(true);
    const onOffline = () => controller.setOnline(false);
    window.addEventListener("keydown", onKey);
    window.addEventListener("pointerdown", onActivity);
    window.addEventListener("keydown", onActivity);
    window.addEventListener("online", onOnline);
    window.addEventListener("offline", onOffline);
    return () => {
      window.removeEventListener("keydown", onKey);
      window.removeEventListener("pointerdown", onActivity);
      window.removeEventListener("keydown", onActivity);
      window.removeEventListener("online", onOnline);
      window.removeEventListener("offline", onOffline);
    };
  }, [controller]);

  // On tablets the list is an overlay: close it once a room is chosen.
  useEffect(() => {
    if (room && window.matchMedia?.(tabletQuery).matches) controller.setListOpen(false);
  }, [room, controller]);

  useEffect(() => localStorage.setItem("mode.listWidth", String(listWidth)), [listWidth]);

  if (bootstrapError) {
    return (
      <div className={styles.shell} style={{ display: "block" }}>
        <EmptyState icon={<AlertTriangle />} title="Mode could not load" action={<Button variant="primary" onClick={() => void controller.bootstrap()}>Try again</Button>}>
          {bootstrapError}
        </EmptyState>
      </div>
    );
  }
  if (!bootstrapped) return <ShellSkeleton />;

  const startResize = (e: PointerEvent<HTMLDivElement>) => {
    const startX = e.clientX;
    const startWidth = listWidth;
    const move = (ev: globalThis.PointerEvent) => setListWidth(Math.min(MAX_LIST, Math.max(MIN_LIST, startWidth + ev.clientX - startX)));
    const up = () => {
      window.removeEventListener("pointermove", move);
      window.removeEventListener("pointerup", up);
    };
    window.addEventListener("pointermove", move);
    window.addEventListener("pointerup", up);
  };
  const resizeWithKeys = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key === "ArrowLeft") setListWidth((w) => Math.max(MIN_LIST, w - 16));
    if (e.key === "ArrowRight") setListWidth((w) => Math.min(MAX_LIST, w + 16));
  };

  const showsMain = section === "files" || section === "admin" || room !== null || searchQuery !== null;
  const list = section === "activity" ? <ActivityList /> : section === "chat" ? <ChatsList /> : section === "channels" ? <ChannelsList /> : null;
  const main = searchQuery !== null ? <SearchView query={searchQuery} />
    : section === "files" ? <FilesView />
    : section === "admin" ? <AdminView />
    : room ? <RoomPane key={`${room.type}:${room.id}`} room={room} />
    : <WelcomePane />;

  return (
    <RadixTooltip.Provider>
      <div
        ref={shellRef}
        className={styles.shell}
        data-section={section}
        data-list-collapsed={!listOpen}
        data-list-open={listOpen}
        data-mobile-view={showsMain ? "main" : "list"}
        style={{ ["--m-list-width" as string]: `${listWidth}px` }}
        onKeyDown={(e) => {
          if (e.key === "Escape" && side && !e.defaultPrevented) controller.setSide(null);
        }}
      >
        <div className={styles.banner}><ConnectionBanner /></div>
        <NavRail />
        {list && (
          <aside className={styles.list} aria-label={section === "activity" ? "Activity" : section === "chat" ? "Chats" : "Channels"}>
            {list}
          </aside>
        )}
        {list && listOpen && (
          <div role="separator" tabIndex={0} aria-orientation="vertical" aria-label="Resize list"
            aria-valuemin={MIN_LIST} aria-valuemax={MAX_LIST} aria-valuenow={listWidth}
            className={styles.resizer} style={{ left: `calc(var(--m-rail-width) + ${listWidth}px)` }}
            onPointerDown={startResize} onKeyDown={resizeWithKeys} />
        )}
        <main className={styles.main}><ErrorBoundary resetKey={`${section}:${room?.id}:${searchQuery}`}>{main}</ErrorBoundary></main>
        {side && room && (
          <aside className={styles.side} aria-label="Details">
            <ErrorBoundary resetKey={JSON.stringify(side)}><SidePanel room={room} side={side} /></ErrorBoundary>
          </aside>
        )}
        <Toasts />
        <CommandBar />
        <DlpWarningDialog />
      </div>
    </RadixTooltip.Provider>
  );
}

export const shellStyles = styles;
