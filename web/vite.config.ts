import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';

export default defineConfig({
  plugins: [react()],
  base: './',
  resolve: { alias: { "@": path.resolve(__dirname, "./src") } },
  build: {
    outDir: '../src/LoadingAI/Web/dist',
    emptyOutDir: true,
    sourcemap: false
  },
  server: {
    port: 5173
  }
});
