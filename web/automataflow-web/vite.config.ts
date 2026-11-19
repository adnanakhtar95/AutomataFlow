import react from '@vitejs/plugin-react';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vitest/config';

// The dashboard talks to the Api SAME-ORIGIN only (the Api has no CORS, by design): in development and preview the
// /api prefix is proxied to the Api; in deployments a reverse proxy does the same (Phase 7).
const api = process.env.AF_API_URL ?? 'http://127.0.0.1:5000';
const proxy = { '/api': { target: api, changeOrigin: false } };

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: { proxy },
  preview: { proxy, host: process.env.AF_PREVIEW_HOST ?? '127.0.0.1', port: 4173, strictPort: true },
  build: { sourcemap: false },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    restoreMocks: true,
  },
});
