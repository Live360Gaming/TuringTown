// A browser can keep an old copy of the game after the server has a new one, and the old copy then misses screens the
// server asks for. Before the game starts, the page checks the build the server has and reloads once if it differs.

const TRIED_KEY = 'turingtown.reloadedFor';

/** Reload when the server has another build, unless this tab already reloaded for that build (so it can never loop). */
export function shouldReload(served, mine, alreadyTriedFor) {
    return !!served && !!mine && served !== mine && alreadyTriedFor !== served;
}

/** Resolves when the game may start. A failed check (dev server, offline) never holds it up. */
export async function ensureLatestBuild() {
    const mine = typeof __BUILD_ID__ === 'undefined' ? null : __BUILD_ID__;
    if (!mine) return;
    try {
        const response = await fetch('version.json', {cache: 'no-store'});
        if (!response.ok) return;
        const {build} = await response.json();
        let tried = null;
        try {
            tried = sessionStorage.getItem(TRIED_KEY);
        } catch {
            // No storage: still safe, the tried check just cannot be remembered.
        }
        if (!shouldReload(build, mine, tried)) return;
        try {
            sessionStorage.setItem(TRIED_KEY, build);
        } catch {
            return; // Without storage a reload could loop, so carry on with what we have.
        }
        location.reload();
        await new Promise(() => {}); // The page is going away.
    } catch {
        // Carry on with what we have.
    }
}
