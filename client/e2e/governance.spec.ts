import { expect, test } from "@playwright/test";
import { expectAccessible, signIn, startChat, unique } from "./helpers";

test("an admin adds a warning rule and a sender confirms before it goes out", async ({ page }) => {
  const word = `falcon${unique()}`;
  await signIn(page, "Ada Okafor", { admin: true });

  await page.getByRole("button", { name: "Admin", exact: true }).click();
  await expect(page.getByRole("heading", { level: 1, name: "Admin" })).toBeVisible();
  await page.getByRole("tab", { name: "Data loss" }).click();
  await page.getByRole("button", { name: "Add pattern" }).click();
  const names = page.getByLabel("Name", { exact: true });
  await names.last().fill("Codename");
  await page.getByLabel("Pattern", { exact: true }).last().fill(word);
  await page.getByRole("button", { name: "Save", exact: true }).click();
  await expect(page.getByRole("status").filter({ hasText: "Saved." })).toBeVisible();
  await expectAccessible(page);

  try {
    await startChat(page);
    const composer = page.getByRole("textbox", { name: "Message Bola Adeyemi" });
    await composer.pressSequentially(`launch ${word} today`);
    await composer.press("Enter");

    const dialog = page.getByRole("dialog", { name: "Check before sending" });
    await expect(dialog.getByText("Matched: Codename")).toBeVisible();
    await dialog.getByRole("button", { name: "Send anyway" }).click();
    await expect(page.locator("article", { hasText: `launch ${word} today` }).locator("[role=alert]")).toHaveCount(0);
    await expect(page.getByText(`launch ${word} today`)).toBeVisible();
  } finally {
    await page.getByRole("button", { name: "Admin", exact: true }).click();
    await page.getByRole("tab", { name: "Data loss" }).click();
    const row = page.getByLabel("Pattern", { exact: true });
    const count = await row.count();
    for (let i = count - 1; i >= 0; i--) {
      if ((await row.nth(i).inputValue()) === word) await page.getByRole("button", { name: "Remove" }).nth(i).click();
    }
    await page.getByRole("button", { name: "Save", exact: true }).click();
    await expect(page.getByRole("status").filter({ hasText: "Saved." })).toBeVisible();
  }
});
