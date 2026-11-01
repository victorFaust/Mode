import { Hash, MessageSquare, Search } from "lucide-react";
import { useController, useMode } from "../app/context";
import { Button, EmptyState } from "../components/ui";

export function WelcomePane() {
  const controller = useController();
  const name = useMode((s) => s.me?.displayName.split(" ")[0] ?? "");
  const section = useMode((s) => s.section);
  return (
    <EmptyState icon={section === "chat" ? <MessageSquare /> : <Hash />} title={name ? `Welcome, ${name}` : "Welcome"}
      action={<Button onClick={() => controller.openCommandBar(true)}><Search size={16} aria-hidden /> Jump to… <kbd style={{ marginLeft: 4 }}>Ctrl K</kbd></Button>}>
      {section === "chat" ? "Pick a chat on the left, or start a new one." : "Pick a channel on the left to see what your team is talking about."}
    </EmptyState>
  );
}
