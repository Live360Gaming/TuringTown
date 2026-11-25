// The TV's narrator: one NarrationService per Phaser game, wired to Phaser's sound system and the music. Phaser's master
// volume (core/hostSettings.js) scales it like every other sound. Only the host uses it; phones never load narration.
import {audioKey, narrationEntry} from '../../narration/registry.js';
import {AUDIO_MIX, getMusic} from '../music.js';
import {scopeFor} from './scopes.js';
import {NarrationService} from './service.js';

const REGISTRY_KEY = 'narration';
const DUCK_OWNER = 'narration';

export function getNarration(game) {
    let narration = game.registry.get(REGISTRY_KEY);
    if (!narration) {
        narration = new NarrationService({
            exists: key => game.cache.audio.exists(audioKey(key)),
            // Browsers block audio until the page has been clicked; Phaser unlocks on the host's first click.
            isLocked: () => !!game.sound.locked,
            durationMs: key => narrationEntry(key).durationMs,
            create: (key, volume) => {
                const sound = game.sound.add(audioKey(key), {volume: volume * AUDIO_MIX.narrationVolume});
                return {
                    play: onEnd => {
                        sound.once('complete', onEnd);
                        return sound.play();
                    },
                    setVolume: value => sound.setVolume(value * AUDIO_MIX.narrationVolume),
                    dispose: () => {
                        sound.stop();
                        sound.destroy();
                    },
                };
            },
            duck: on => getMusic(game).duck(on, DUCK_OWNER),
            setTimeout: (callback, ms) => window.setTimeout(callback, ms),
            clearTimeout: id => window.clearTimeout(id),
            gapMs: AUDIO_MIX.narrationGapMs,
            // Keys only, never anything about players.
            log: (event, key) => console.debug(`narration: ${event} ${key}`),
        });
        game.registry.set(REGISTRY_KEY, narration);
    }
    return narration;
}

/** Stops stale lines as the server moves the game on. Returns a function that stops following and goes quiet. */
export function followNarration(game, session) {
    const narration = getNarration(game);
    const onState = state => narration.enterScope(scopeFor(state));
    session.on('state', onState);
    if (session.state) onState(session.state);
    return () => {
        session.off('state', onState);
        narration.reset();
    };
}
