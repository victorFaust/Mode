import { useEffect, useId, useState, type FormEvent, type ReactNode } from "react";
import { ok, type Channel, type RoomRef, type User } from "../api/client";
import { useController, useMode } from "../app/context";
import { Button, Dialog, DialogActions, TextField, ui } from "./ui";
import { PeoplePicker } from "./PeoplePicker";

function useSubmit(action: () => Promise<void>, onDone: () => void) {
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const submit = async (e?: FormEvent) => {
    e?.preventDefault();
    setBusy(true);
    setError(null);
    try {
      await action();
      onDone();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Something went wrong.");
    } finally {
      setBusy(false);
    }
  };
  return { busy, error, submit, setError };
}

function Form({ onSubmit, children }: { onSubmit: (e: FormEvent) => void; children: ReactNode }) {
  return <form onSubmit={onSubmit} style={{ display: "grid", gap: 16 }}>{children}</form>;
}

export function NewWorkspaceDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const controller = useController();
  const [name, setName] = useState("");
  const { busy, error, submit } = useSubmit(() => controller.createWorkspace(name.trim()), () => onOpenChange(false));
  useEffect(() => { if (open) setName(""); }, [open]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="New workspace" description="A workspace groups a team's channels. It starts with a General channel.">
      <Form onSubmit={submit}>
        <TextField label="Name" value={name} onChange={setName} maxLength={100} autoFocus error={error} name="workspace-name" />
        <DialogActions>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="primary" type="submit" disabled={busy || !name.trim()}>Create</Button>
        </DialogActions>
      </Form>
    </Dialog>
  );
}

export function NewChannelDialog({ workspaceId, onClose }: { workspaceId: string | null; onClose: () => void }) {
  const controller = useController();
  const workspaces = useMode((s) => s.workspaces);
  const [target, setTarget] = useState("");
  const [name, setName] = useState("");
  const [description, setDescription] = useState("");
  const [type, setType] = useState<Channel["type"]>("Standard");
  const id = useId();
  const { busy, error, submit } = useSubmit(
    () => controller.createChannel(target, name.trim(), type, description.trim() || null), onClose);

  useEffect(() => {
    if (workspaceId) {
      setTarget(workspaceId);
      setName("");
      setDescription("");
      setType("Standard");
    }
  }, [workspaceId]);

  return (
    <Dialog open={workspaceId !== null} onOpenChange={(open) => !open && onClose()} title="New channel">
      <Form onSubmit={submit}>
        <div className={ui.field}>
          <label className={ui.label} htmlFor={`${id}-ws`}>Workspace</label>
          <select id={`${id}-ws`} className={ui.input} value={target} onChange={(e) => setTarget(e.target.value)}>
            {workspaces.filter((w) => w.myRole !== "Guest").map((w) => <option key={w.id} value={w.id}>{w.name}</option>)}
          </select>
        </div>
        <TextField label="Name" value={name} onChange={setName} maxLength={80} autoFocus name="channel-name" />
        <TextField label="Description" value={description} onChange={setDescription} maxLength={1000} hint="Optional" />
        <fieldset style={{ border: "none", padding: 0, margin: 0, display: "grid", gap: 8 }}>
          <legend className={ui.label}>Who can see it</legend>
          <div className={ui.segmented} role="radiogroup" aria-label="Privacy">
            {(["Standard", "Private"] as const).map((t) => (
              <button key={t} type="button" role="radio" aria-checked={type === t} className={ui.chip} onClick={() => setType(t)}>
                {t === "Standard" ? "Everyone in the workspace" : "Only people I add"}
              </button>
            ))}
          </div>
        </fieldset>
        {error && <p role="alert" className={ui.error}>{error}</p>}
        <DialogActions>
          <Button variant="ghost" onClick={onClose}>Cancel</Button>
          <Button variant="primary" type="submit" disabled={busy || !name.trim() || !target}>Create</Button>
        </DialogActions>
      </Form>
    </Dialog>
  );
}

export function NewChatDialog({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const controller = useController();
  const [people, setPeople] = useState<User[]>([]);
  const [name, setName] = useState("");
  const { busy, error, submit } = useSubmit(
    () => controller.startConversation(people.map((p) => p.id), people.length > 1 ? name.trim() || null : null),
    () => onOpenChange(false));
  useEffect(() => {
    if (open) {
      setPeople([]);
      setName("");
    }
  }, [open]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="New chat">
      <Form onSubmit={submit}>
        <PeoplePicker selected={people} onChange={setPeople} label="To" />
        {people.length > 1 && <TextField label="Group name" value={name} onChange={setName} maxLength={100} hint="Optional" />}
        {error && <p role="alert" className={ui.error}>{error}</p>}
        <DialogActions>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="primary" type="submit" disabled={busy || people.length === 0}>Start chat</Button>
        </DialogActions>
      </Form>
    </Dialog>
  );
}

/** Adds a person to a channel (must already be in the workspace) or a group chat. */
export function AddMemberDialog({ room, existing, open, onOpenChange, onAdded }: {
  room: RoomRef; existing: string[]; open: boolean; onOpenChange: (open: boolean) => void; onAdded: () => void;
}) {
  const controller = useController();
  const [people, setPeople] = useState<User[]>([]);
  const { busy, error, submit } = useSubmit(async () => {
    for (const person of people) {
      const body = { userId: person.id };
      if (room.type === "Channel")
        await ok(controller.api.POST("/api/v1/channels/{channelId}/members", { params: { path: { channelId: room.id } }, body }));
      else
        await ok(controller.api.POST("/api/v1/conversations/{conversationId}/members", { params: { path: { conversationId: room.id } }, body }));
    }
    onAdded();
  }, () => onOpenChange(false));
  useEffect(() => { if (open) setPeople([]); }, [open]);

  return (
    <Dialog open={open} onOpenChange={onOpenChange} title="Add people">
      <Form onSubmit={submit}>
        <PeoplePicker selected={people} onChange={setPeople} exclude={existing} />
        {error && <p role="alert" className={ui.error}>{error}</p>}
        <DialogActions>
          <Button variant="ghost" onClick={() => onOpenChange(false)}>Cancel</Button>
          <Button variant="primary" type="submit" disabled={busy || people.length === 0}>Add</Button>
        </DialogActions>
      </Form>
    </Dialog>
  );
}
