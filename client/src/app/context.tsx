import { createContext, useContext, type ReactNode } from "react";
import { useStore } from "zustand";
import type { ModeController } from "../store/controller";
import type { ModeState } from "../store/store";

interface ModeContextValue {
  controller: ModeController;
  /** Element inside the Mode root where dialogs and popovers render, so theme tokens apply to them. */
  portal: HTMLElement | null;
}

const ModeContext = createContext<ModeContextValue | null>(null);

export function ModeProvider({ value, children }: { value: ModeContextValue; children: ReactNode }) {
  return <ModeContext.Provider value={value}>{children}</ModeContext.Provider>;
}

function useModeContext(): ModeContextValue {
  const value = useContext(ModeContext);
  if (!value) throw new Error("Mode components must be rendered inside ModeProvider.");
  return value;
}

export function useController(): ModeController {
  return useModeContext().controller;
}

export function usePortal(): HTMLElement | null {
  return useModeContext().portal;
}

/** Select primitives or stable references; wrap derived objects with useShallow. */
export function useMode<T>(selector: (state: ModeState) => T): T {
  return useStore(useModeContext().controller.store, selector);
}
