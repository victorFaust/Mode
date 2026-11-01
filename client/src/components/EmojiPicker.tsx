import * as Popover from "@radix-ui/react-popover";
import { Smile } from "lucide-react";
import { lazy, Suspense, useState, type ReactNode } from "react";
import { usePortal } from "../app/context";
import { IconButton, Skeleton } from "./ui";

/** emoji-mart and its data (~400 KB) load only when the picker is first opened. */
const Picker = lazy(async () => {
  const [{ default: EmojiMart }, { default: data }] = await Promise.all([import("@emoji-mart/react"), import("@emoji-mart/data")]);
  function LoadedPicker(props: { onPick: (emoji: string) => void; theme: string }) {
    return <EmojiMart data={data} onEmojiSelect={(e: { native: string }) => props.onPick(e.native)} theme={props.theme}
      previewPosition="none" skinTonePosition="search" autoFocus />;
  }
  return { default: LoadedPicker };
});

export function EmojiPicker({ onPick, label = "Insert emoji", trigger }: { onPick: (emoji: string) => void; label?: string; trigger?: ReactNode }) {
  const portal = usePortal();
  const [open, setOpen] = useState(false);
  const theme = portal?.closest("[data-theme]")?.getAttribute("data-theme") ?? "light";
  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      <Popover.Trigger asChild>
        {trigger ?? <IconButton label={label} size="small"><Smile size={18} aria-hidden /></IconButton>}
      </Popover.Trigger>
      <Popover.Portal container={portal}>
        <Popover.Content side="top" align="end" sideOffset={8} style={{ zIndex: 55 }} aria-label="Emoji picker">
          <Suspense fallback={<div style={{ width: 352, height: 380, padding: 16 }}><Skeleton width="100%" height={340} /></div>}>
            <Picker theme={theme} onPick={(emoji) => { onPick(emoji); setOpen(false); }} />
          </Suspense>
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}
