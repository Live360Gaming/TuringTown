import { defineConfig } from 'vite';

// Each build gets an id. It is compiled into the game and written to version.json, so a page that loaded an old build
// can tell (see src/core/version.js).
const buildId = Date.now().toString(36);

export default defineConfig({
    // Uses relative paths so assets load properly under /game/
    base: './',
    define: {__BUILD_ID__: JSON.stringify(buildId)},
    plugins: [{
        name: 'build-id',
        generateBundle() {
            this.emitFile({type: 'asset', fileName: 'version.json', source: JSON.stringify({build: buildId})});
        },
    }],
    build: {
        // Outputs directly into the ASP.NET Core wwwroot/game folder
        outDir: '../wwwroot',
        // Clears old hashed JS/asset files on each build (also suppresses Vite's outside-root warning)
        emptyOutDir: true
    }
});
