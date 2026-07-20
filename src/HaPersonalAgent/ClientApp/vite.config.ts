import { defineConfig } from 'vite';
import preact from '@preact/preset-vite';

// Что: конфигурация сборки SPA (Vite + Preact).
// Зачем: SPA собирается в ../wwwroot, откуда её раздаёт встроенный ASP.NET Core хост (HPA-025).
// Как: base './' даёт относительные пути к ассетам, чтобы страница работала под ingress-префиксом HA; выходная папка — wwwroot.
export default defineConfig({
  plugins: [preact()],
  base: './',
  build: {
    outDir: '../wwwroot',
    emptyOutDir: true,
    chunkSizeWarningLimit: 900,
  },
});
