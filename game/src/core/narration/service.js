// Plays recorded narration, one line at a time. It knows nothing about Phaser: everything it touches is passed in, so
// it can be tested on its own (game/test/narration.test.js). It never drives the game: a line that is missing, blocked
// or silent only means the caption is all there is.

/** How long past a line's own length to wait for it to report its end. */
export const TIMEOUT_SLACK_MS = 1500;

export class NarrationService {
    /**
     * @param deps {exists(key), isLocked(), durationMs(key), create(key, volume) -> {play(onEnd), setVolume(v), dispose()},
     *              duck(on), duckLead() -> ms until the music is down, setTimeout, clearTimeout, gapMs, log(event, key)}
     */
    constructor(deps) {
        this.deps = {gapMs: 350, log: () => {}, duckLead: () => 0, ...deps};
        this.queue = [];
        this.current = null;
        this.gap = null;
        this.scope = null;
        this.played = new Set();
        this.volume = 1;
        this.muted = false;
    }

    canPlay(key) {
        return this.deps.exists(key) && !this.deps.isLocked();
    }

    /** Replaces whatever is playing. Resolves 'completed', 'cancelled', 'timeout', 'missing', 'blocked' or 'duplicate'. */
    play(key, {occurrence} = {}) {
        if (this.seen(occurrence, key)) return Promise.resolve('duplicate');
        this.stop();
        return this.add(key);
    }

    /** Plays `keys` in order after whatever is already playing. */
    enqueue(keys, {occurrence} = {}) {
        if (this.seen(occurrence, keys[0])) return Promise.resolve(keys.map(() => 'duplicate'));
        return Promise.all(keys.map(key => this.add(key)));
    }

    /** An occurrence is one time something happens (one verdict, one goodbye); it is only ever spoken once. */
    seen(occurrence, key) {
        if (!occurrence) return false;
        if (this.played.has(occurrence)) {
            this.deps.log('duplicate skipped', key);
            return true;
        }
        this.played.add(occurrence);
        return false;
    }

    add(key) {
        return new Promise(resolve => {
            this.queue.push({key, resolve});
            this.pump();
        });
    }

    pump() {
        if (this.current || this.gap !== null) return;
        const item = this.queue.shift();
        if (!item) {
            this.deps.duck(false);
            return;
        }

        const {key, resolve} = item;
        const refuse = reason => {
            this.deps.log(reason, key);
            resolve(reason);
            this.pump();
        };
        if (!this.deps.exists(key)) return refuse('missing');
        if (this.deps.isLocked()) return refuse('blocked');

        let sound;
        try {
            sound = this.deps.create(key, this.effectiveVolume());
        } catch {
            return refuse('missing');
        }

        const current = {key, resolve, sound, timer: null};
        this.current = current;
        const end = reason => {
            if (this.current === current) this.finish(reason);
        };
        const speak = () => {
            if (this.current !== current) return;
            // A line that never reports its end must not hold up the ones behind it.
            current.timer = this.deps.setTimeout(() => end('timeout'), this.deps.durationMs(key) + TIMEOUT_SLACK_MS);
            this.deps.log('started', key);
            if (sound.play(() => end('completed')) === false) end('blocked');
        };

        // Turn the music down first, and speak once it is down. Stopping during the wait cancels the line like any other.
        this.deps.duck(true);
        const lead = this.deps.duckLead();
        if (lead > 0) current.timer = this.deps.setTimeout(speak, lead);
        else speak();
    }

    finish(reason) {
        const {key, resolve, sound, timer} = this.current;
        this.current = null;
        this.deps.clearTimeout(timer);
        sound.dispose();
        this.deps.log(reason, key);
        resolve(reason);

        if (reason === 'cancelled') return;
        if (this.queue.length === 0) {
            this.deps.duck(false);
            return;
        }
        this.gap = this.deps.setTimeout(() => {
            this.gap = null;
            this.pump();
        }, this.deps.gapMs);
    }

    clearQueue() {
        this.queue.splice(0).forEach(item => item.resolve('cancelled'));
    }

    /** Silence now: the line playing and everything waiting. */
    stop() {
        this.clearQueue();
        if (this.gap !== null) {
            this.deps.clearTimeout(this.gap);
            this.gap = null;
        }
        if (this.current) this.finish('cancelled');
        this.deps.duck(false);
    }

    /** Stops only if `key` is what is playing, so a closing screen cannot cut off the next screen's line. */
    stopKey(key) {
        if (this.current?.key === key) this.stop();
    }

    /** The game moved to another part: lines from the last one are stale. A new game forgets what has been said. */
    enterScope(scope) {
        if (scope === this.scope) return;
        this.scope = scope;
        this.stop();
        if (scope === 'lobby' || scope === 'tutorial') this.played.clear();
    }

    reset() {
        this.stop();
        this.played.clear();
        this.scope = null;
    }

    setVolume(volume) {
        this.volume = Math.min(1, Math.max(0, volume));
        this.current?.sound.setVolume(this.effectiveVolume());
    }

    setMuted(muted) {
        this.muted = muted;
        this.current?.sound.setVolume(this.effectiveVolume());
    }

    effectiveVolume() {
        return this.muted ? 0 : this.volume;
    }
}
