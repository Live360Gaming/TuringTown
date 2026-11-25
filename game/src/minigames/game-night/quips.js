// The goodbye on the Finale: "Thanks for playing!" with a new quip under it every few seconds while the timer runs.

const QUIPS = [
    'The town will remember this.',
    'No neighbors were permanently deleted.',
    'Trust is rebuilding. Please allow 3 to 5 business days.',
    'Somewhere, a bot is taking notes for next time.',
    'If you were wrongly accused, you may sulk now.',
    'Lock your doors. Check your friends.',
    'Same town tomorrow? Bring a better alibi.',
    'Be honest. You sounded a little robotic too.',
];

const SECONDS = 5;

/** Fills `text` now and keeps rotating it for as long as the scene runs. Starts at a random quip. */
export function rotateQuips(scene, text) {
    let index = Math.floor(Math.random() * QUIPS.length);
    const show = () => text.setText(`Thanks for playing!
${QUIPS[index++ % QUIPS.length]}`);
    show();
    scene.time.addEvent({delay: SECONDS * 1000, loop: true, callback: show});
}
