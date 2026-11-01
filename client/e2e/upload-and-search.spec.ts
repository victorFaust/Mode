import { expect, test } from "@playwright/test";
import { expectAccessible, signIn, startChat, unique } from "./helpers";

test("upload a file in a chat, then find it by search", async ({ page }) => {
  const id = unique();
  const word = `ledger${id}`;
  await signIn(page);
  await startChat(page);

  await page.getByTestId("file-input").setInputFiles({
    name: `reconciliation-${id}.txt`,
    mimeType: "text/plain",
    buffer: Buffer.from(`Month-end ${word} reconciliation for the treasury desk.`),
  });
  await expect(page.getByRole("progressbar", { name: `Uploading reconciliation-${id}.txt` })).toBeVisible();
  await expect(page.getByText(/^Uploaded/)).toBeVisible();

  const composer = page.getByRole("textbox", { name: "Message Bola Adeyemi" });
  await composer.fill("Month-end file attached");
  await composer.press("Enter");

  // The download stays disabled until the virus scan finishes and the realtime update arrives.
  const download = page.getByRole("button", { name: `Download reconciliation-${id}.txt` });
  await expect(download).toBeEnabled({ timeout: 90_000 });

  await page.keyboard.press("Control+k");
  await page.getByPlaceholder(/Jump to a/).fill(word);
  await page.getByRole("option", { name: new RegExp(`Search for “${word}”`) }).click();

  // File names are searchable at once; the text inside is indexed after preview processing, so search again until it is.
  const files = page.getByRole("region", { name: "Files" });
  await expect(async () => {
    await page.getByRole("search").getByRole("button", { name: "Search" }).click();
    await expect(files.locator("mark", { hasText: word })).toBeVisible({ timeout: 3_000 });
  }).toPass({ timeout: 60_000 });
  await expect(files.getByText(`reconciliation-${id}.txt`).first()).toBeVisible();
  await expectAccessible(page);
});
