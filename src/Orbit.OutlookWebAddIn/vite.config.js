import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { defineConfig } from "vite";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const certDir = path.join(os.homedir(), ".office-addin-dev-certs");

function readHttpsOptions() {
  const keyPath = path.join(certDir, "localhost.key");
  const certPath = path.join(certDir, "localhost.crt");
  if (!fs.existsSync(keyPath) || !fs.existsSync(certPath)) {
    console.warn(
      "[orbit-outlook-web-addin] Missing localhost certs in ~/.office-addin-dev-certs — run `npm run certs` (admin may be required to trust the CA).",
    );
    return undefined;
  }

  return {
    key: fs.readFileSync(keyPath),
    cert: fs.readFileSync(certPath),
  };
}

export default defineConfig(() => {
  const https = readHttpsOptions();
  return {
    root: __dirname,
    server: {
      https,
      port: 3000,
      strictPort: true,
      headers: {
        "Access-Control-Allow-Origin": "*",
      },
    },
    preview: {
      https,
      port: 3000,
      strictPort: true,
    },
    build: {
      outDir: "dist",
      emptyOutDir: true,
      rollupOptions: {
        input: {
          taskpane: path.resolve(__dirname, "taskpane.html"),
          commands: path.resolve(__dirname, "commands.html"),
        },
      },
    },
  };
});
