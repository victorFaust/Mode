import { useEffect, useState } from "react";
import { useController, useMode } from "../app/context";
import { Button, Dialog, DialogActions, TextField } from "./ui";

export function StatusMessageDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const controller = useController();
  const presence = useMode((s) => (s.me ? s.presence[s.me.id] : undefined));
  const [message, setMessage] = useState("");
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (open) {
      setMessage(presence?.message ?? "");
      setError(null);
    }
  }, [open, presence?.message]);

  const save = async (value: string | null) => {
    try {
      await controller.setPresence(presence?.status === "Offline" || !presence ? "Available" : presence.status, value);
      onOpenChange(false);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Could not save.");
    }
  };

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Status message" description="Shown to people you work with next to your name.">
      <form onSubmit={(e) => { e.preventDefault(); void save(message.trim() || null); }} style={{ display: "grid", gap: 16 }}>
        <TextField label="Message" value={message} onChange={setMessage} maxLength={280} autoFocus placeholder="In a meeting until 3pm" error={error} />
        <DialogActions>
          <Button variant="ghost" onClick={() => void save(null)}>Clear</Button>
          <Button variant="primary" type="submit">Save</Button>
        </DialogActions>
      </form>
    </Dialog>
  );
}
