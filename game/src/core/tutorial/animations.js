import {CHARACTER_KEYS} from '../../constants.js';

// Named animations a tutorial step can ask for (`animation: 'pulse'` in the game's tutorial data), so the data
// stays plain and the code stays here. Each one adds its objects to `layer` and returns a function that stops it.
// These are placeholders to be replaced with real animations.
export const TUTORIAL_ANIMATIONS = {
    // A circle that breathes in and out.
    pulse(scene, layer, {cx, cy}) {
        const circle = scene.add.circle(cx, cy - 20, 90, 0x3355aa).setStrokeStyle(6, 0xffffff);
        layer.add(circle);
        const tween = scene.tweens.add({targets: circle, scale: 1.4, yoyo: true, repeat: -1, duration: 700});
        return () => tween.remove();
    },

    // Four characters that keep swapping places.
    shuffle(scene, layer, {cx, cy}) {
        const xs = [cx - 270, cx - 90, cx + 90, cx + 270];
        const faces = CHARACTER_KEYS.slice(0, 4).map((key, i) => scene.add.image(xs[i], cy - 20, key).setDisplaySize(150, 150));
        layer.add(faces);

        let order = [...xs];
        const timer = scene.time.addEvent({
            delay: 1200,
            loop: true,
            callback: () => {
                order = [...order.slice(1), order[0]];
                faces.forEach((face, i) => scene.tweens.add({targets: face, x: order[i], duration: 500}));
            },
        });

        return () => {
            timer.remove();
            scene.tweens.killTweensOf(faces);
        };
    },
};
