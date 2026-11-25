// The one list of recorded narration is ./manifest.json: key, category, file (under public/audio), title, transcript and
// duration. The server embeds the same file (Services/Games/NarrationManifest.cs). The transcripts are the script the
// lines were recorded from; they have not been checked against the audio.
import entries from './manifest.json';

/** @typedef {{key: string, category: 'introduction'|'transition'|'ending'|'goodbye', file: string, title: string, transcript: string, durationMs: number}} NarrationEntry */

const BY_KEY = new Map(entries.map(entry => [entry.key, entry]));

/** The entry for `key`. Throws on a key that does not exist, so a typo fails loudly. @returns {NarrationEntry} */
export function narrationEntry(key) {
    const entry = BY_KEY.get(key);
    if (!entry) throw new Error(`Unknown narration key "${key}"`);
    return entry;
}

export const audioKey = key => `tt_vo_${narrationEntry(key).key}`;

/** Audio key -> file under public/audio, for the TV's loader (phones never load narration). */
export const NARRATION_FILES = Object.fromEntries(entries.map(entry => [`tt_vo_${entry.key}`, entry.file]));
