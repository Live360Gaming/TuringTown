// The seasonal look (for example Christmas), chosen by the server (GET /api/config) and loaded before the game starts.
// A season only lists the files it replaces (assets/seasons/<season>/manifest.json, key -> file); anything it does not
// list keeps the normal art, so a game without seasonal art is unaffected. Keys are the same texture keys the scenes use.

let season = 'default';
let files = {};

/** Asks the server which season to show and reads its manifest. Never throws: any failure means the normal look. */
export async function loadSeason(timeoutMs = 2500) {
    try {
        const get = async (url) => {
            const res = await fetch(url, {signal: AbortSignal.timeout(timeoutMs)});
            if (!res.ok) throw new Error(`${url}: ${res.status}`);
            return res.json();
        };
        const config = await get(`${window.location.origin}/api/config`);
        if (config.season && config.season !== 'default') {
            files = await get(`assets/seasons/${config.season}/manifest.json`);
            season = config.season;
        }
    } catch (err) {
        console.warn('Season unavailable, using the normal look.', err);
        season = 'default';
        files = {};
    }
    applyFavicon();
    return season;
}

export const currentSeason = () => season;

/**
 * Where to load a texture from, relative to the `assets/` folder: the season's file when it has one, else `normal`.
 * Use it for every asset a season may replace.
 */
export function assetPath(key, normal) {
    return files[key] ? `seasons/${season}/${files[key]}` : normal;
}

function applyFavicon() {
    if (!files.favicon) return;
    const link = document.querySelector('link[rel="icon"]');
    if (link) link.href = `assets/seasons/${season}/${files.favicon}`;
}
