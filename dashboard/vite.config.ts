import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';
import { fileURLToPath } from 'node:url';

export default defineConfig({
  root: fileURLToPath(new URL('./client', import.meta.url)),
  plugins: [svelte()],
  build: { outDir: '../dist', emptyOutDir: true },
  server: { host: '127.0.0.1', proxy: { '/api': 'http://127.0.0.1:8080', '/healthz': 'http://127.0.0.1:8080' } },
});
