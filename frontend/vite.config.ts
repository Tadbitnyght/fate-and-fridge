import react from '@vitejs/plugin-react'
import { loadEnv } from 'vite'
import { defineConfig } from 'vitest/config'

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  // Development proxy target for the ASP.NET Core backend; override with
  // VITE_API_PROXY (shell or .env) when the backend runs elsewhere.
  const apiProxyTarget = env.VITE_API_PROXY || 'http://localhost:50080'

  return {
    plugins: [react()],
    server: {
      port: 5173,
      proxy: {
        '/api': {
          target: apiProxyTarget,
          changeOrigin: true,
        },
        // SignalR hub (docs/M9_PLAN.md §1.2): the SPA upgrades the socket
        // directly, so the dev proxy must forward WebSocket traffic too.
        '/hub/live': {
          target: apiProxyTarget,
          changeOrigin: true,
          ws: true,
        },
      },
    },
    build: {
      outDir: 'dist',
      sourcemap: true,
    },
    test: {
      environment: 'jsdom',
      setupFiles: ['./src/test/setup.ts'],
      css: false,
      include: ['src/**/*.test.{ts,tsx}'],
    },
  }
})
