const HAPTICS_KEY = 'turingtown.haptics';

// Used when localStorage is unavailable (private window etc.), so the toggle still works until the page reloads.
let memoryValue = null;

/** True when this browser can vibrate (Android Chrome/Firefox; iPhones cannot). */
export function hapticsSupported() {
    return typeof navigator !== 'undefined' && typeof navigator.vibrate === 'function';
}

/** Whether this phone's buzz is on. It is saved per device and defaults to on. */
export function hapticsEnabled() {
    if (memoryValue !== null) return memoryValue;
    try {
        return localStorage.getItem(HAPTICS_KEY) !== 'off';
    } catch {
        return true;
    }
}

/** Turns this phone's buzz on or off and returns the new value. */
export function setHapticsEnabled(on) {
    memoryValue = on;
    try {
        localStorage.setItem(HAPTICS_KEY, on ? 'on' : 'off');
    } catch {
        // Storage unavailable; the in-memory value above still applies.
    }
    if (!on) navigator.vibrate?.(0);
    return on;
}

/** Buzzes the phone (ms or a pattern) if it can and the player has not turned it off. */
export function buzz(pattern = 60) {
    if (!hapticsSupported() || !hapticsEnabled()) return;
    navigator.vibrate(pattern);
}
