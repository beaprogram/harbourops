import tailwindcss from '@tailwindcss/vite'
import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

// During development the browser talks only to Vite (localhost:5173). Vite forwards /api calls
// to the ASP.NET Core API, so cookies and antiforgery behave exactly like the single-origin
// production setup, and no CORS configuration is needed.
const apiTarget = process.env.HARBOUROPS_API_URL ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      '/api': { target: apiTarget },
      '/health': { target: apiTarget },
    },
  },
  build: {
    sourcemap: false,
    // The licences of every dependency bundled into the shipped JavaScript, served at /third-party-licenses.txt
    // (.txt because ASP.NET Core's static files don't serve .md). The minified bundle itself keeps no notices.
    license: { fileName: 'third-party-licenses.txt' },
    rolldownOptions: {
      output: {
        // Long-lived vendor chunks change rarely, so browsers keep them cached across app releases.
        codeSplitting: {
          groups: [
            { name: 'react', test: /[\\/]node_modules[\\/](react|react-dom|scheduler|react-router)[\\/]/, priority: 20 },
            { name: 'vendor', test: /[\\/]node_modules[\\/]/, priority: 10 },
          ],
        },
      },
    },
  },
})
