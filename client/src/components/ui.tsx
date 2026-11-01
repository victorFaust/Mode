import * as RadixDialog from "@radix-ui/react-dialog";
import * as RadixMenu from "@radix-ui/react-dropdown-menu";
import * as RadixTooltip from "@radix-ui/react-tooltip";
import { Clock, Minus } from "lucide-react";
import { forwardRef, useId, type ButtonHTMLAttributes, type ReactNode } from "react";
import type { PresenceStatus } from "../api/client";
import { usePortal } from "../app/context";
import { initials } from "../lib/format";
import styles from "./ui.module.css";

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & { variant?: "default" | "primary" | "ghost" | "danger" };

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button({ variant = "default", className, ...props }, ref) {
  const variantClass = variant === "default" ? "" : styles[variant];
  return <button ref={ref} type="button" className={`${styles.button} ${variantClass} ${className ?? ""}`} {...props} />;
});

type IconButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & { label: string; size?: "small" | "normal"; tooltip?: boolean };

/** An icon-only button: the label is its accessible name and its tooltip. */
export const IconButton = forwardRef<HTMLButtonElement, IconButtonProps>(function IconButton(
  { label, size = "normal", tooltip = true, className, children, ...props }, ref) {
  const button = (
    <button ref={ref} type="button" aria-label={label}
      className={`${styles.iconButton} ${size === "small" ? styles.small : ""} ${className ?? ""}`} {...props}>
      {children}
    </button>
  );
  return tooltip ? <Tooltip label={label}>{button}</Tooltip> : button;
});

export function Tooltip({ label, children, side = "top" }: { label: string; children: ReactNode; side?: "top" | "right" | "bottom" }) {
  const portal = usePortal();
  return (
    <RadixTooltip.Root delayDuration={400}>
      <RadixTooltip.Trigger asChild>{children}</RadixTooltip.Trigger>
      <RadixTooltip.Portal container={portal}>
        <RadixTooltip.Content side={side} sideOffset={6} className={styles.tooltip}>{label}</RadixTooltip.Content>
      </RadixTooltip.Portal>
    </RadixTooltip.Root>
  );
}

const avatarColors = ["#8b1911", "#1f5f8b", "#2e6b3a", "#6b3fa0", "#8a4b0f", "#2f4f6f", "#7a2048", "#0f5f5f"];

function colorFor(id: string): string {
  let hash = 0;
  for (const char of id) hash = (hash * 31 + char.charCodeAt(0)) | 0;
  return avatarColors[Math.abs(hash) % avatarColors.length];
}

const statusLabels: Record<PresenceStatus, string> = {
  Available: "Available", Busy: "Busy", DoNotDisturb: "Do not disturb", Away: "Away", Offline: "Offline",
};

export function presenceLabel(status: PresenceStatus): string {
  return statusLabels[status];
}

/** Presence is shown by shape and icon as well as colour. */
export function PresenceDot({ status }: { status: PresenceStatus }) {
  return (
    <span className={styles.presence} data-status={status} role="img" aria-label={statusLabels[status]}>
      {status === "DoNotDisturb" && <Minus strokeWidth={4} aria-hidden />}
      {status === "Away" && <Clock strokeWidth={3} aria-hidden />}
    </span>
  );
}

export function Avatar({ id, name, avatarUrl, size = 32, status }: {
  id: string; name: string; avatarUrl?: string | null; size?: number; status?: PresenceStatus;
}) {
  return (
    <span className={styles.avatar} style={{ width: size, height: size, background: colorFor(id), fontSize: Math.round(size * 0.4) }} aria-hidden={!status}>
      {avatarUrl ? <img src={avatarUrl} alt="" /> : initials(name)}
      {status && <PresenceDot status={status} />}
    </span>
  );
}

export function Badge({ count, muted, label }: { count: number; muted?: boolean; label: string }) {
  if (count <= 0) return null;
  return (
    <span className={`${styles.badge} ${muted ? styles.badgeMuted : ""}`} aria-label={label}>
      {count > 99 ? "99+" : count}
    </span>
  );
}

export function Skeleton({ width, height = 12, radius }: { width: number | string; height?: number; radius?: number }) {
  return <span className={styles.skeleton} style={{ width, height, borderRadius: radius }} aria-hidden />;
}

export function EmptyState({ icon, title, children, action }: { icon: ReactNode; title: string; children?: ReactNode; action?: ReactNode }) {
  return (
    <div className={styles.empty}>
      <span className={styles.emptyIcon} aria-hidden>{icon}</span>
      <p className={styles.emptyTitle}>{title}</p>
      {children && <p className={styles.emptyBody}>{children}</p>}
      {action}
    </div>
  );
}

export function Dialog({ open, onOpenChange, title, description, children }: {
  open: boolean; onOpenChange: (open: boolean) => void; title: string; description?: string; children: ReactNode;
}) {
  const portal = usePortal();
  return (
    <RadixDialog.Root open={open} onOpenChange={onOpenChange}>
      <RadixDialog.Portal container={portal}>
        <RadixDialog.Overlay className={styles.overlay} />
        <RadixDialog.Content className={styles.dialog}>
          <RadixDialog.Title className={styles.dialogTitle}>{title}</RadixDialog.Title>
          {description
            ? <RadixDialog.Description className={styles.dialogDescription}>{description}</RadixDialog.Description>
            : <RadixDialog.Description className="m-visually-hidden">{title}</RadixDialog.Description>}
          {children}
        </RadixDialog.Content>
      </RadixDialog.Portal>
    </RadixDialog.Root>
  );
}

export function DialogActions({ children }: { children: ReactNode }) {
  return <div className={styles.dialogActions}>{children}</div>;
}

export function TextField({ label, hint, error, value, onChange, autoFocus, placeholder, maxLength, name }: {
  label: string; hint?: string; error?: string | null; value: string; onChange: (value: string) => void;
  autoFocus?: boolean; placeholder?: string; maxLength?: number; name?: string;
}) {
  const id = useId();
  return (
    <div className={styles.field}>
      <label className={styles.label} htmlFor={id}>{label}</label>
      <input id={id} name={name} className={styles.input} value={value} autoFocus={autoFocus} placeholder={placeholder}
        maxLength={maxLength} onChange={(e) => onChange(e.target.value)}
        aria-invalid={error ? true : undefined} aria-describedby={hint || error ? `${id}-hint` : undefined} />
      {(hint || error) && <span id={`${id}-hint`} className={error ? styles.error : styles.hint}>{error ?? hint}</span>}
    </div>
  );
}

export function Menu({ trigger, children, align = "start", onOpenChange }: {
  trigger: ReactNode; children: ReactNode; align?: "start" | "end"; onOpenChange?: (open: boolean) => void;
}) {
  const portal = usePortal();
  return (
    <RadixMenu.Root onOpenChange={onOpenChange}>
      <RadixMenu.Trigger asChild>{trigger}</RadixMenu.Trigger>
      <RadixMenu.Portal container={portal}>
        <RadixMenu.Content className={styles.menu} align={align} sideOffset={6}>{children}</RadixMenu.Content>
      </RadixMenu.Portal>
    </RadixMenu.Root>
  );
}

export function MenuItem({ children, onSelect, disabled, danger }: {
  children: ReactNode; onSelect: () => void; disabled?: boolean; danger?: boolean;
}) {
  return (
    <RadixMenu.Item className={`${styles.menuItem} ${danger ? styles.danger : ""}`} onSelect={onSelect} disabled={disabled}>
      {children}
    </RadixMenu.Item>
  );
}

export const MenuSeparator = () => <RadixMenu.Separator className={styles.menuSeparator} />;
export const MenuLabel = ({ children }: { children: ReactNode }) => <RadixMenu.Label className={styles.menuLabel}>{children}</RadixMenu.Label>;

export const ui = styles;
