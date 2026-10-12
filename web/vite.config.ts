import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { fileURLToPath, URL } from 'node:url';

// The service (VuStudio.Server) rejects cross-origin requests, so the dev proxy rewrites the
// Origin header to the service's own origin. Start the service with:
//   dotnet run --project src/VuStudio.Server -- --simulate 4 --data-dir ./.vustudio
const target = process.env.VUSTUDIO_URL ?? 'http://127.0.0.1:5341';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target,
        ws: true,
        changeOrigin: true,
        configure: (proxy) => {
          proxy.on('proxyReq', (req) => req.setHeader('origin', target));
          proxy.on('proxyReqWs', (req) => req.setHeader('origin', target));
        },
      },
    },
  },
  build: {
    outDir: '../src/VuStudio.Server/wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 1500,
  },
});
