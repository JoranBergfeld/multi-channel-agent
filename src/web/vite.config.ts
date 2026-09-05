import react from '@vitejs/plugin-react'
// `defineConfig` comes from `vitest/config` (a superset of Vite's) so the same config file can
// carry both the build/dev settings and the `test` block Vitest reads - without this, Vitest and
// Vite would need separate config files that could drift out of sync.
import { defineConfig } from 'vitest/config'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/testing/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    // Every stubbed global (installFakeEventSource uses `vi.stubGlobal`) is torn down between
    // tests automatically, so one test's fake `EventSource` can never leak into the next.
    unstubGlobals: true,
  },
})
