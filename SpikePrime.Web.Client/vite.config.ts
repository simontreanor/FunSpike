import { defineConfig } from 'vite'
import solidPlugin from 'vite-plugin-solid'

// https://vitejs.dev/config/
export default defineConfig({
    clearScreen: false,
    server: {
        port: 5173,
        // Proxy WebSocket to the Oxpecker backend during development
        proxy: {
            '/ws': {
                target: 'ws://localhost:5000',
                ws: true,
                changeOrigin: true
            }
        },
        watch: {
            ignored: ['**/*.md', '**/*.fs', '**/*.fsx']
        }
    },
    build: {
        // Output built files to the backend's wwwroot so it can serve them
        outDir: '../SpikePrime.Web/wwwroot',
        emptyOutDir: true
    },
    plugins: [solidPlugin()]
})
