import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// The UI is a separate deployable. In development it proxies /api to a locally running
// service so nothing is cross-origin; in the Compose stack nginx does the same. Point
// ADMIN_API_TARGET elsewhere to develop against another instance.
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': { target: process.env.ADMIN_API_TARGET ?? 'http://localhost:8082', changeOrigin: true } },
  },
  build: { outDir: 'dist', sourcemap: false },
  test: { environment: 'node', include: ['src/**/*.test.ts'] },
});
