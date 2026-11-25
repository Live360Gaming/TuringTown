import {listen} from './stage/listen.js';

/** 75000 -> "1:15" */
export function formatMs(ms) {
    const seconds = Math.ceil(ms / 1000);
    return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

/**
 * Keeps `text` showing how long is left in the current stage, using the server's clock (see GameSession.msLeft),
 * so every screen in the room counts down together. Empty when the stage has no deadline.
 */
export function attachCountdown(scene, session, text) {
    const update = () => {
        const ms = session.msLeft();
        text.setText(ms === null ? '' : formatMs(ms));
    };

    // The scene's clock is cleaned up with the scene, so only the session listener needs removing.
    scene.time.addEvent({delay: 250, loop: true, callback: update});
    listen(scene, session, 'state', update);

    update();
    return update;
}
