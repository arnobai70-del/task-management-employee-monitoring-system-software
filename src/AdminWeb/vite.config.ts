import { defineConfig, loadEnv } from 'vite';

export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, '.', 'VITE_');
  const target = env.VITE_DEV_API_TARGET || 'http://localhost:5080';
  return {
    server: {
      port: 5173,
      proxy: {
        '/api': { target, changeOrigin: true, secure: false },
        '/hubs': { target, changeOrigin: true, secure: false, ws: true }
      }
    }
  };
});
