import { defineConfig } from 'vite';

export default defineConfig({
  server: {
    host: "127.0.0.1",
    proxy: {
      "/api": { target: "http://127.0.0.1:5295", changeOrigin: false },
      "/hubs": { target: "http://127.0.0.1:5295", ws: true, changeOrigin: false },
    },
    // Pinned so the preview URL is stable; change it here if 5195 is taken.
    port: 5195,
    strictPort: true,
  },

  build: {
    target: 'es2020',
    outDir: 'dist',
    assetsInlineLimit: 0,
    rollupOptions: {
      output: {
        // Three.js in its own chunk so the serif headline paints before any
        // WebGL code is parsed; GSAP and Lenis share a small motion chunk.
        manualChunks(id) {
          if (id.includes('/node_modules/three/')) return 'three';
          if (id.includes('/node_modules/gsap/') || id.includes('/node_modules/lenis/')) return 'motion';
        },
      },
    },
  },
});
