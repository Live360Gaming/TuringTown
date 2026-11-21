// Background music for the TV: one track at a time, crossfaded, never restarted by a repeat request.
//
// There is one MusicPlayer per Phaser game (getMusic). It is not tied to a scene, so a track keeps playing while the
// director switches screens. Fades run on the game's step, so they pause with the game when the tab is hidden.
// Every track is still scaled by Phaser's master volume (core/hostSettings.js), so the host's slider applies at once.
//
// Only the host uses it (see GameSession.attachDirector); phones never play music.

const REGISTRY_KEY = 'music';
const DEFAULT_FADE_MS = 1500;
const DUCK_FACTOR = Math.pow(10, -8 / 20); // -8 dB, about 0.40
const DUCK_FADE_MS = 300;

/**
 * One playing track. `level` is the fade position (0-1) and `duck` the ducking multiplier, both moved toward their
 * targets on every step; the sound's volume is `base * level * duck`.
 */
class Track {
    constructor(sound, base) {
        this.sound = sound;
        this.key = sound.key;
        this.base = base;
        this.level = 0;
        this.levelTarget = 0;
        this.levelRate = 0; // per ms
        this.duck = 1;
        this.duckTarget = 1;
    }

    fadeTo(target, ms) {
        this.levelTarget = target;
        this.levelRate = ms > 0 ? 1 / ms : Infinity;
    }

    step(delta) {
        this.level = approach(this.level, this.levelTarget, this.levelRate * delta);
        this.duck = approach(this.duck, this.duckTarget, (1 - DUCK_FACTOR) / DUCK_FADE_MS * delta);
        this.sound.volume = this.base * this.level * this.duck;
    }
}

const approach = (value, target, amount) =>
    value < target ? Math.min(target, value + amount) : Math.max(target, value - amount);

export class MusicPlayer {
    constructor(game) {
        this.game = game;
        /** The track that is (or is about to be) playing, kept after a one-shot ends so it is not replayed. */
        this.currentKey = null;
        this.current = null;
        this.outgoing = [];
        this.ducked = false;
        /** A track asked for before its audio finished loading: {key, options}. */
        this.pending = null;

        this.onStep = (_time, delta) => this.step(delta);
        this.onCacheAdd = (_cache, key) => {
            if (this.pending?.key === key) {
                const {options} = this.pending;
                this.pending = null;
                this.start(key, options);
            }
        };
        game.events.on('step', this.onStep);
        game.cache.audio.events.on('add', this.onCacheAdd);
    }

    /**
     * Plays `key`, crossfading from whatever is playing. Does nothing if `key` is already the current track, so it
     * is safe to call on every state update.
     * @param options {loop = true, volume = 1, fadeMs = 1500, fadeInMs = fadeMs}
     */
    play(key, options = {}) {
        if (key === this.currentKey) return;
        this.currentKey = key;
        this.pending = null;
        this.releaseCurrent(options.fadeMs ?? DEFAULT_FADE_MS);

        if (!this.game.cache.audio.exists(key)) {
            // Still loading (see Host): start it as soon as it arrives.
            this.pending = {key, options};
            return;
        }
        this.start(key, options);
    }

    /** Fades out whatever is playing. */
    stop(fadeMs = DEFAULT_FADE_MS) {
        this.currentKey = null;
        this.pending = null;
        this.releaseCurrent(fadeMs);
    }

    /** Lowers the music by about 8 dB (e.g. under spoken narration), or restores it. */
    duck(on) {
        this.ducked = on;
        const target = on ? DUCK_FACTOR : 1;
        if (this.current) this.current.duckTarget = target;
    }

    destroy() {
        this.game.events.off('step', this.onStep);
        this.game.cache.audio.events.off('add', this.onCacheAdd);
        [this.current, ...this.outgoing].forEach(track => track?.sound.destroy());
        this.current = null;
        this.outgoing = [];
    }

    start(key, {loop = true, volume = 1, fadeMs = DEFAULT_FADE_MS, fadeInMs = fadeMs} = {}) {
        // Still fading out from a moment ago: bring it back instead of starting a second copy.
        let track = this.outgoing.find(t => t.key === key);
        if (track) {
            this.outgoing = this.outgoing.filter(t => t !== track);
            track.base = volume;
        } else {
            const sound = this.game.sound.add(key, {loop, volume: 0});
            track = new Track(sound, volume);
            if (!loop) {
                // A one-shot frees itself when it ends but stays the current key, so it is not played again.
                sound.once('complete', () => {
                    if (this.current === track) this.current = null;
                    sound.destroy();
                });
            }
            sound.play();
        }
        track.duckTarget = this.ducked ? DUCK_FACTOR : 1;
        track.fadeTo(1, fadeInMs);
        this.current = track;
    }

    releaseCurrent(fadeMs) {
        if (!this.current) return;
        this.current.fadeTo(0, fadeMs);
        this.outgoing.push(this.current);
        this.current = null;
    }

    step(delta) {
        this.current?.step(delta);
        for (const track of this.outgoing) track.step(delta);
        // Tracks that have faded out are gone for good.
        this.outgoing = this.outgoing.filter(track => {
            if (track.level > 0) return true;
            track.sound.destroy();
            return false;
        });
    }
}

/** The game's MusicPlayer, created on first use. */
export function getMusic(game) {
    let music = game.registry.get(REGISTRY_KEY);
    if (!music) {
        music = new MusicPlayer(game);
        game.registry.set(REGISTRY_KEY, music);
    }
    return music;
}
