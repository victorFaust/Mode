import { expect, test } from "@playwright/test";
import { expectAccessible, signIn, startChat, unique } from "./helpers";

test("pin a message, forward it to another chat, and links only open for members", async ({ page }) => {
  const id = unique();
  await signIn(page);
  await startChat(page, "Chidi Nwosu");
  await startChat(page);

  const composer = page.getByRole("textbox", { name: "Message Bola Adeyemi" });
  await composer.pressSequentially(`Branch audit ${id} starts Monday`);
  await composer.press("Enter");
  const feed = page.getByRole("log", { name: "Messages in Bola Adeyemi" });
  const message = feed.getByRole("article").filter({ hasText: id });
  await expect(message.locator("time")).not.toHaveText("Sending…");
  const messageId = (await message.locator("[id^='msg-']").getAttribute("id"))!.replace("msg-", "");

  await message.hover();
  await message.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: "Pin" }).click();
  const pinned = page.getByRole("region", { name: "Pinned messages" });
  await expect(pinned).toContainText(`Branch audit ${id}`);
  await expect(message.getByText("Pinned", { exact: true })).toBeVisible();
  await expectAccessible(page);

  await message.hover();
  await message.getByRole("button", { name: "More actions" }).click();
  await page.getByRole("menuitem", { name: "Forward" }).click();
  const dialog = page.getByRole("dialog", { name: "Forward message" });
  await dialog.getByRole("radio", { name: "Chidi Nwosu" }).check();
  await dialog.getByLabel("Add a note (optional)").fill(`Note ${id}`);
  await expectAccessible(page);
  await dialog.getByRole("button", { name: "Forward" }).click();
  await expect(dialog).toBeHidden();

  // Unpin so repeated runs stay under the per-chat limit.
  await pinned.getByRole("button", { name: /pinned/ }).click();
  const unpin = pinned.getByRole("button", { name: /^Unpin message/ });
  while (await unpin.count() > 0) {
    const before = await unpin.count();
    await unpin.first().click();
    await expect(unpin).toHaveCount(before - 1);
  }
  await expect(pinned).toBeHidden();

  // Chidi sees the forwarded card but can't open the original in Ada and Bola's chat.
  await signIn(page, "Chidi Nwosu");
  await page.getByRole("button", { name: /^Ada Okafor/ }).first().click();
  const card = page.getByRole("log", { name: "Messages in Ada Okafor" }).getByRole("article").filter({ hasText: `Note ${id}` });
  await expect(card).toContainText("Forwarded from Ada Okafor");
  await expect(card).toContainText(`Branch audit ${id} starts Monday`);
  await card.getByRole("button", { name: "View original" }).click();
  await expect(page.getByText("Can't open that message")).toBeVisible();

  await page.evaluate((target) => { location.hash = `mode-message=${target}`; }, messageId);
  await expect(page.getByRole("heading", { level: 2, name: "Bola Adeyemi" })).toHaveCount(0);
});
