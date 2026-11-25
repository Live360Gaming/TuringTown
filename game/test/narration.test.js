import assert from 'node:assert/strict';
import {test} from 'node:test';
import {endingKey, scopeFor, truthKey} from '../src/core/narration/scopes.js';
import {NarrationService} from '../src/core/narration/service.js';

/** A service on a fake clock with fake sounds. `end()` finishes the line that is playing. */
function setup({missing = [], locked = false} = {}) {
    const log = {played: [], disposed: [], duck: false, volumes: []};
    const timers = new Map();
    let nextId = 1;
    let ended = null;
    const service = new NarrationService({
        exists: key => !missing.includes(key),
        isLocked: () => locked,
        durationMs: () => 1000,
        create: key => ({
            play: onEnd => {
                log.played.push(key);
                ended = onEnd;
                return true;
            },
            setVolume: value => log.volumes.push(value),
            dispose: () => log.disposed.push(key),
        }),
        duck: on => log.duck = on,
        setTimeout: callback => {
            timers.set(nextId, callback);
            return nextId++;
        },
        clearTimeout: id => timers.delete(id),
        gapMs: 10,
    });
    const tick = () => {
        const [id, callback] = timers.entries().next().value;
        timers.delete(id);
        callback();
    };
    return {service, log, timers, tick, end: () => ended()};
}

test('queued lines play in order, never together, with the music ducked until the last ends', async () => {
    const t = setup();
    const done = t.service.enqueue(['goodbye_thanks', 'goodbye_robotic']);
    assert.deepEqual(t.log.played, ['goodbye_thanks']);
    assert.equal(t.log.duck, true);

    t.end();
    t.tick(); // the gap between lines
    assert.deepEqual(t.log.played, ['goodbye_thanks', 'goodbye_robotic']);
    t.end();

    assert.deepEqual(await done, ['completed', 'completed']);
    assert.equal(t.log.duck, false);
    assert.equal(t.timers.size, 0);
});

test('the same occurrence is spoken once; a new game may say it again', async () => {
    const t = setup();
    t.service.enterScope('ending');
    t.service.enqueue(['bots_win'], {occurrence: 'ending'});
    assert.deepEqual(await t.service.enqueue(['bots_win'], {occurrence: 'ending'}), ['duplicate']);
    assert.deepEqual(t.log.played, ['bots_win']);

    t.service.enterScope('lobby');
    t.service.enqueue(['bots_win'], {occurrence: 'ending'});
    assert.deepEqual(t.log.played, ['bots_win', 'bots_win']);
});

test('moving on cancels the line and what was waiting, and brings the music back', async () => {
    const t = setup();
    t.service.enterScope('finale');
    const done = t.service.enqueue(['goodbye_thanks', 'goodbye_robotic']);
    t.service.enterScope('lobby');

    assert.deepEqual(await done, ['cancelled', 'cancelled']);
    assert.deepEqual(t.log.disposed, ['goodbye_thanks']);
    assert.equal(t.log.duck, false);
    assert.equal(t.timers.size, 0);
});

test('a missing or blocked line is skipped without holding anything up', async () => {
    const missing = setup({missing: ['rapid']});
    assert.equal(await missing.service.play('rapid'), 'missing');
    assert.equal(missing.log.duck, false);

    assert.equal(await setup({locked: true}).service.play('rapid'), 'blocked');
});

test('a line that never reports its end times out', async () => {
    const t = setup();
    const done = t.service.play('survey');
    t.tick();
    assert.equal(await done, 'timeout');
    assert.equal(t.log.duck, false);
});

test('a closing screen only stops its own line, and muting does not stop playback', () => {
    const t = setup();
    t.service.play('tap');
    t.service.stopKey('rapid');
    assert.deepEqual(t.log.disposed, []);
    t.service.setMuted(true);
    assert.deepEqual(t.log.volumes, [0]);
    t.service.stopKey('tap');
    assert.deepEqual(t.log.disposed, ['tap']);
});

test('the server result picks exactly one ending line and the truth line only when there is one', () => {
    assert.equal(endingKey({eliminated: {id: 1}, wasBot: true}), 'humans_win');
    assert.equal(endingKey({eliminated: {id: 1}, wasBot: false}), 'bots_win');
    assert.equal(endingKey({eliminated: null, wasBot: null}), 'no_verdict');
    assert.equal(truthKey(true), 'truth_theirs');
    assert.equal(truthKey(false), 'truth_not_theirs');
    assert.equal(truthKey(null), null);
    assert.equal(truthKey(undefined), null);
});

test('the verdict and the results are one part, so the ending runs on into the truth line', () => {
    assert.equal(scopeFor({stage: 'Reveal', round: 3, data: {final: true}}), scopeFor({stage: 'Results', round: 0}));
    assert.notEqual(scopeFor({stage: 'Reveal', round: 1, data: {final: false}}), 'ending');
    assert.notEqual(scopeFor({stage: 'Transition', round: 2, data: {beat: 'hotseat'}}), scopeFor({stage: 'Transition', round: 2, data: {beat: 'answer'}}));
});
