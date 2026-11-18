const VOLUME_KEY = 'turingtown.volume';
const DEFAULT_VOLUME = 100;

const clamp = v => Math.min(100, Math.max(0, Math.round(v)));

/** Saved host volume (0-100). Falls back to 100 if nothing is saved or storage is unavailable. */
export function getVolume() {
    try {
        const raw = localStorage.getItem(VOLUME_KEY);
        if (raw === null) return DEFAULT_VOLUME;
        const n = Number(raw);
        return Number.isFinite(n) ? clamp(n) : DEFAULT_VOLUME;
    } catch {
        return DEFAULT_VOLUME;
    }
}

/** Saves the volume (0-100) and returns the clamped value. */
export function setVolume(value) {
    const v = clamp(value);
    try {
        localStorage.setItem(VOLUME_KEY, String(v));
    } catch {
        // Storage unavailable (private window etc.); the volume still applies for this session.
    }
    return v;
}

/**
 * Applies the volume to Phaser's global sound manager. This is the single master volume: every sound
 * (current and future) played through Phaser's sound system is scaled by it. Host-only; phones are separate pages.
 */
export function applyVolume(game, value = getVolume()) {
    game.sound.volume = clamp(value) / 100;
}
