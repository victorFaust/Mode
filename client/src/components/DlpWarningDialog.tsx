import { useController, useMode } from "../app/context";
import { Button, Dialog, DialogActions } from "./ui";

/** Shown when a message matches the organization's warning rules; sending anyway is the sender's choice. */
export function DlpWarningDialog() {
  const controller = useController();
  const warning = useMode((s) => s.dlpWarning);
  return (
    <Dialog open={warning !== null} onOpenChange={(open) => !open && controller.dismissDlpWarning()}
      title="Check before sending" description="This message may contain sensitive information.">
      {warning && warning.rules.length > 0 && (
        <p>Matched: {warning.rules.join(", ")}</p>
      )}
      <DialogActions>
        <Button onClick={() => controller.dismissDlpWarning()}>Go back</Button>
        <Button variant="primary" onClick={() => {
          const confirm = warning?.confirm;
          controller.dismissDlpWarning();
          confirm?.();
        }}>Send anyway</Button>
      </DialogActions>
    </Dialog>
  );
}
