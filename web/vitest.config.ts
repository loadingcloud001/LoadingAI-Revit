import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    // Pure-logic tests only. The React pane is verified by `npm run typecheck` + `npm run build`;
    // everything in src/lib must stay free of React, window, and the host bridge to land here.
    include: ['src/**/*.test.ts'],
    environment: 'node'
  }
});
