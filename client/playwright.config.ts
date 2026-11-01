import { defineConfig, devices } from "@playwright/test";

/**
 * End-to-end flows against the real stack. Start the Docker services first:
 *   docker compose up -d postgres clamav file-processor mock-issuer
 * The API and the dev shell are started here (or reused if already running).
 */
export default defineConfig({
  testDir: "e2e",
  timeout: 120_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  // The flows share seeded users and their direct chat.
  workers: 1,
  reporter: [["list"]],
  use: {
    baseURL: "http://localhost:5173",
    trace: "retain-on-failure",
    ...devices["Desktop Chrome"],
    viewport: { width: 1366, height: 860 },
  },
  webServer: [
    {
      command: "dotnet run --project ../src/Mode.Api --urls http://localhost:5080",
      url: "http://localhost:5080/health/ready",
      reuseExistingServer: true,
      timeout: 180_000,
    },
    {
      command: "npm run dev",
      url: "http://localhost:5173",
      reuseExistingServer: true,
      timeout: 60_000,
    },
  ],
});
