import { defineConfig } from 'vite';
import { osrDemandPlugin } from './scripts/osr-demand-plugin';

export default defineConfig({
  base: './',
  plugins: [osrDemandPlugin()],
  optimizeDeps: { exclude: ['osr-emu'] },
  server: { port: 5173 },
  build: { outDir: 'dist', emptyOutDir: true },
});
