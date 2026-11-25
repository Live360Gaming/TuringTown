import assert from 'node:assert/strict';
import {test} from 'node:test';
// The emitter Phaser's own is built on.
import EventEmitter from 'eventemitter3';
import {listen} from '../src/core/stage/listen.js';

const fakeScene = () => {
    const scene = {events: new EventEmitter()};
    scene.shutdown = () => scene.events.emit('shutdown');
    return scene;
};

test('a screen closed by an earlier listener of the same event is not called', () => {
    const session = new EventEmitter();
    const scene = fakeScene();
    let calls = 0;
    // The director is registered first and closes the old screen inside the event.
    session.on('state', () => scene.shutdown());
    listen(scene, session, 'state', () => calls++);

    session.emit('state');

    assert.equal(calls, 0);
});

test('a running screen is called with the scene as this, and again after it restarts', () => {
    const session = new EventEmitter();
    const scene = fakeScene();
    const seen = [];
    const subscribe = () => listen(scene, session, 'state', function (value) {
        seen.push([this === scene, value]);
    });

    subscribe();
    session.emit('state', 1);
    scene.shutdown();
    session.emit('state', 2);
    subscribe();
    session.emit('state', 3);

    assert.deepEqual(seen, [[true, 1], [true, 3]]);
    assert.equal(session.listenerCount('state'), 1);
});
