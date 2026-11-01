import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

const env = (globalThis as { process?: { env: Record<string, string | undefined> } }).process?.env ?? {};
const api = env.MODE_API_URL ?? "http://localhost:5080";
const issuer = env.MODE_ISSUER_URL ?? "http://localhost:5100";

export default defineConfig(({ mode }) => ({
  plugins: [react()],
  server: {
    port: 5173,
    strictPort: true,
    // Same-origin in development: no CORS needed for the dev shell.
    proxy: {
      "/api": { target: api, changeOrigin: true },
      "/hubs": { target: api, changeOrigin: true, ws: true },
      "/issuer": { target: issuer, changeOrigin: true, rewrite: (path) => path.replace(/^\/issuer/, "") },
    },
  },
  build: mode === "lib"
    ? {
        // The embeddable module for the host application: dist-lib/mode.js and mode.css.
        outDir: "dist-lib",
        lib: { entry: "src/index.tsx", formats: ["es"], fileName: () => "mode.js", cssFileName: "mode" },
      }
    : { outDir: "dist" },
  test: {
    environment: "jsdom",
    setupFiles: ["src/test/setup.ts"],
    include: ["src/**/*.test.{ts,tsx}"],
  },
}));
