import { defineConfig } from 'vite';

export default defineConfig({
    // Uses relative paths so assets load properly under /game/
    base: './',
    build: {
        // Outputs directly into the ASP.NET Core wwwroot/game folder
        outDir: '../wwwroot',
        // Clears old hashed JS/asset files on each build (also suppresses Vite's outside-root warning)
        emptyOutDir: true
    }
});
