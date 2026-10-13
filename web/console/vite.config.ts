import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';
import { SECURITY_HEADERS } from './csp.ts';

// `npm run dev` proxies the API to a running server, so the page and the API share one origin as they do when embedded.
const server = process.env.CHARGEHAND_URL ?? 'http://127.0.0.1:4300';

export default defineConfig({
  // The server serves the build at /console/ (ADR 0056).
  base: '/console/',
  plugins: [react()],
  build: {
    outDir: 'dist',
    // Nothing is inlined into the page or the scripts: the policy allows no inline script or style.
    assetsInlineLimit: 0,
    sourcemap: false,
    // One script is served as is, uncompressed, over a private network (ADR 0056); it was 342 KB, with the design system 526 KB.
    chunkSizeWarningLimit: 700,
  },
  server: { port: 5173, proxy: { '/v1': server } },
  preview: { host: '127.0.0.1', port: 4173, strictPort: true, headers: SECURITY_HEADERS },
  test: {
    environment: 'happy-dom',
    include: ['src/**/*.test.{ts,tsx}'],
    setupFiles: ['src/test-setup.ts'],
  },
});
