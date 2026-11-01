import { expect, test } from "@playwright/test";
import { expectAccessible, signIn, startChat, unique } from "./helpers";

test("edit a sent message, delete it and undo the delete", async ({ page }) => {
  const id = unique();
  await signIn(page);
  await startChat(page);

  const composer = page.getByRole("textbox", { name: "Message Bola Adeyemi" });
  await composer.pressSequentially(`Draft ${id} has a typo`);
  await composer.press("Enter");

  const feed = page.getByRole("log", { name: "Messages in Bola Adeyemi" });
  const sent = feed.getByRole("article").filter({ hasText: id });
  await expect(sent).toBeVisible();
  await expect(sent.locator("time")).not.toHaveText("Sending…");
  const metaId = await sent.locator("[id^='msg-']").getAttribute("id");
  const message = feed.getByRole("article").filter({ has: page.locator(`[id='${metaId}']`) });

  await message.hover();
  await message.getByRole("button", { name: "Edit", exact: true }).click();
  const editBox = message.getByRole("textbox", { name: "Edit message" });
  await editBox.click();
  await page.keyboard.press("Control+End");
  await editBox.pressSequentially(" (*fixed*)");
  await editBox.press("Enter");

  await expect(message.locator("em", { hasText: "fixed" })).toBeVisible();
  await expect(message.getByText("Edited")).toBeVisible();

  await message.hover();
  await message.getByRole("button", { name: "Delete", exact: true }).click();
  await page.getByRole("dialog").getByRole("button", { name: "Delete" }).click();
  await expect(message.getByText("This message was deleted.")).toBeVisible();
  await expectAccessible(page);

  await message.getByRole("button", { name: "Undo" }).click();
  await expect(message.locator("em", { hasText: "fixed" })).toBeVisible();
  await expect(message.getByText("This message was deleted.")).toHaveCount(0);
});
