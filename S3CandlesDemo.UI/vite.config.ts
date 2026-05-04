import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/candles': {
        target: 'http://localhost:5044',
        changeOrigin: true,
      },
    },
  },
})
