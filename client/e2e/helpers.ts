import AxeBuilder from "@axe-core/playwright";
import { expect, type Page } from "@playwright/test";

/** Signs in through the dev shell, which gets a token from the mock issuer. */
export async function signIn(page: Page, name = "Ada Okafor", { admin = false } = {}): Promise<void> {
  await page.goto("/");
  await page.evaluate(() => sessionStorage.clear());
  await page.goto("/");
  await page.getByLabel("Sign in as").selectOption({ label: name });
  if (admin) await page.getByLabel("Mode admin").check();
  await page.getByRole("button", { name: "Sign in" }).click();
  await expect(page.getByRole("navigation", { name: "Mode" })).toBeVisible({ timeout: 60_000 });
}

/** Opens (or reuses) the direct chat with a seeded colleague through the New chat dialog. */
export async function startChat(page: Page, person = "Bola Adeyemi"): Promise<void> {
  await page.getByRole("button", { name: "Chat", exact: true }).click();
  await page.getByRole("button", { name: "New chat" }).click();
  const dialog = page.getByRole("dialog");
  await dialog.getByRole("combobox", { name: "To" }).fill(person.split(" ")[0]);
  await dialog.getByRole("option", { name: new RegExp(person) }).click();
  await dialog.getByRole("button", { name: "Start chat" }).click();
  await expect(page.getByRole("heading", { level: 2, name: person })).toBeVisible();
}

/** Fails on serious or critical WCAG A/AA problems in what is currently on screen. */
export async function expectAccessible(page: Page): Promise<void> {
  const results = await new AxeBuilder({ page }).include("[data-mode-root]").withTags(["wcag2a", "wcag2aa", "wcag21aa"]).analyze();
  const serious = results.violations.filter((v) => v.impact === "serious" || v.impact === "critical");
  expect(serious.map((v) => `${v.id}: ${v.nodes.map((n) => `${n.html} ${n.failureSummary ?? ""}`).join(" | ")}`)).toEqual([]);
}

export const unique = () => Date.now().toString(36) + Math.random().toString(36).slice(2, 6);
