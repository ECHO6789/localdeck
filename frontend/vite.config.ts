import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'
import { resolve } from 'node:path'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  base: './',
  build: {
    outDir: resolve(import.meta.dirname, '../src/LocalDeck.App/wwwroot'),
    emptyOutDir: true,
  },
})
