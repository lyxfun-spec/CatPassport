import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [react()],
  server: {
    watch: {
      // The .NET backend compiles next door while the app boots; those obj/bin writes
      // otherwise trigger Vite full-page reloads right after the renderer mounts.
      ignored: ['**/node_modules/**', '**/.git/**', '**/.vite/**', '**/.artifacts/**', '**/out/**', '**/dist/**',
        '**/obj/**', '**/bin/**', '../CatAssistant.Backend/**', '../CatAssistant.Core/**', '../CatAssistant.Backend.Tests/**', '../CatAssistant.Core.Tests/**']
    }
  }
});
