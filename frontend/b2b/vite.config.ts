import tailwindcss from '@tailwindcss/vite';
import react from '@vitejs/plugin-react';
import { defineConfig, loadEnv } from 'vite';

// The browser only ever talks to the Commercial API. In development the Vite server proxies
// /api and /health to it, so application code uses relative URLs (no CORS, no hard-coded hosts).
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '');
  const apiTarget = env.VITE_API_PROXY_TARGET || 'http://localhost:5080';

  return {
    plugins: [react(), tailwindcss()],
    server: {
      port: 5174,
      strictPort: true,
      proxy: {
        '/api': { target: apiTarget, changeOrigin: true },
        '/health': { target: apiTarget, changeOrigin: true },
      },
    },
    preview: { port: 5174 },
    build: { sourcemap: true },
  };
});
