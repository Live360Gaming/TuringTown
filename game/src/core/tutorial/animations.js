import {CHARACTER_KEYS, ROBOT_KEYS} from '../../constants.js';
import {COLORS, HEX, textStyle} from '../theme.js';

// Named animations a tutorial step can ask for (`animation: 'town'` in the game's tutorial data), so the data
// stays plain and the code stays here. Each one adds its objects to `layer` and returns a function that stops it
// (the TutorialPlayer then destroys the layer's objects).
//
// The robots drawn here are illustrations of "a bot", never a player's avatar, so they do not go through createAvatar.

/** Tracks the tweens and timers an animation starts, so one call stops them all. */
function runner(scene) {
    const tweens = [];
    const timers = [];
    return {
        tween(config) {
            const tween = scene.tweens.add(config);
            tweens.push(tween);
            return tween;
        },
        every(delay, callback) {
            timers.push(scene.time.addEvent({delay, loop: true, callback}));
        },
        after(delay, callback) {
            timers.push(scene.time.delayedCall(delay, callback));
        },
        stop() {
            timers.forEach(timer => timer.remove());
            tweens.forEach(tween => tween.remove());
        },
    };
}

/** A sprite at (x, y), `size` px square, added to the layer. */
function sprite(scene, layer, key, x, y, size) {
    const image = scene.add.image(x, y, key).setDisplaySize(size, size);
    layer.add(image);
    return image;
}

/** A gentle up-and-down bob, offset per index so a row does not move in lockstep. */
function bob(run, target, i, height = 10) {
    run.tween({targets: target, y: target.y - height, duration: 900, ease: 'Sine.easeInOut', yoyo: true, repeat: -1, delay: i * 180});
}

/** A white speech bubble with a small tail, centred on (x, y). */
function bubble(scene, layer, x, y, message) {
    const text = scene.add.text(0, 0, message, textStyle('bubble', {fontSize: '22px'})).setOrigin(0.5);
    const w = text.width + 32;
    const h = text.height + 18;
    const bg = scene.add.graphics();
    bg.fillStyle(0xffffff, 1).fillRoundedRect(-w / 2, -h / 2, w, h, 14);
    bg.fillTriangle(-10, h / 2 - 1, 10, h / 2 - 1, 0, h / 2 + 14);
    const container = scene.add.container(x, y, [bg, text]);
    layer.add(container);
    return container;
}

export const TUTORIAL_ANIMATIONS = {
    // Nothing, for a step that draws its own picture.
    blank() {
        return () => {};
    },

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

    // A little skyline with flickering windows, and five residents bobbing in front of it.
    town(scene, layer, {cx, cy}) {
        const run = runner(scene);
        const ground = cy + 70;
        const houses = [
            {x: -470, w: 130, h: 170}, {x: -320, w: 110, h: 230}, {x: -180, w: 150, h: 150}, {x: -20, w: 120, h: 260},
            {x: 130, w: 140, h: 180}, {x: 290, w: 110, h: 220}, {x: 430, w: 150, h: 160},
        ];
        const windows = [];
        houses.forEach(({x, w, h}) => {
            const left = cx + x - w / 2;
            const house = scene.add.rectangle(cx + x, ground - h / 2, w, h, HEX.surface).setStrokeStyle(3, HEX.border);
            const roof = scene.add.triangle(cx + x, ground - h - 22, 0, 44, w / 2 + 12, 0, w + 24, 44, HEX.surfaceRaised);
            layer.add([house, roof]);
            for (let row = 0; row < Math.floor((h - 30) / 46); row++) {
                for (let col = 0; col < 2; col++) {
                    const win = scene.add.rectangle(left + w * (col ? 0.7 : 0.3), ground - h + 30 + row * 46, 22, 26, HEX.gold)
                        .setAlpha(Math.random() < 0.6 ? 1 : 0.15);
                    windows.push(win);
                    layer.add(win);
                }
            }
        });
        layer.add(scene.add.rectangle(cx, ground + 4, 1100, 8, HEX.border));

        const residents = CHARACTER_KEYS.slice(0, 5).map((key, i) => sprite(scene, layer, key, cx + (i - 2) * 190, cy + 20, 140));
        residents.forEach((r, i) => bob(run, r, i));

        run.every(450, () => {
            const win = windows[Math.floor(Math.random() * windows.length)];
            win?.setAlpha(win.alpha > 0.5 ? 0.15 : 1);
        });
        return () => run.stop();
    },

    // One "resident" keeps glitching, and for a moment you can see the robot underneath.
    disguise(scene, layer, {cx, cy}) {
        const run = runner(scene);
        const human = CHARACTER_KEYS[2];
        const left = sprite(scene, layer, CHARACTER_KEYS[0], cx - 300, cy, 150);
        const right = sprite(scene, layer, CHARACTER_KEYS[4], cx + 300, cy, 150);
        const suspect = sprite(scene, layer, human, cx, cy - 10, 210);
        const mark = scene.add.text(cx, cy - 150, '?', textStyle('title', {fontSize: '64px', color: COLORS.cyan})).setOrigin(0.5).setAlpha(0);
        layer.add(mark);
        bob(run, left, 0);
        bob(run, right, 2);

        run.every(2400, () => {
            run.tween({targets: suspect, x: cx + 10, duration: 45, yoyo: true, repeat: 5, onComplete: () => suspect.setX(cx)});
            suspect.setTint(HEX.cyan);
            run.after(270, () => {
                suspect.clearTint().setTexture(ROBOT_KEYS[0]).setDisplaySize(210, 210);
                mark.setAlpha(1);
            });
            run.after(900, () => {
                suspect.setTexture(human).setDisplaySize(210, 210);
                mark.setAlpha(0);
            });
        });
        return () => run.stop();
    },

    // A survey card fills itself in, then gets stamped PRIVATE.
    survey(scene, layer, {cx, cy}) {
        const run = runner(scene);
        const y0 = cy - 20;
        const card = scene.add.graphics();
        card.fillStyle(HEX.surfaceRaised, 1).fillRoundedRect(cx - 220, y0 - 170, 440, 340, 20);
        card.lineStyle(4, HEX.cyan, 1).strokeRoundedRect(cx - 220, y0 - 170, 440, 340, 20);
        const header = scene.add.text(cx, y0 - 128, 'TOWN SURVEY', textStyle('title', {fontSize: '30px', color: COLORS.gold})).setOrigin(0.5);
        layer.add([card, header]);

        const rows = [0, 1, 2].map(i => {
            const y = y0 - 50 + i * 72;
            const box = scene.add.rectangle(cx - 160, y, 36, 36, HEX.bgDeep).setStrokeStyle(3, HEX.border);
            const tick = scene.add.text(cx - 160, y, '✓', textStyle('name', {fontSize: '30px', color: COLORS.bgDeep})).setOrigin(0.5).setVisible(false);
            const line = scene.add.rectangle(cx - 120, y, 220 - i * 40, 14, HEX.textDim).setOrigin(0, 0.5).setScale(0, 1);
            layer.add([box, tick, line]);
            return {box, tick, line};
        });
        const stamp = scene.add.text(cx + 70, y0 + 110, 'PRIVATE', textStyle('title', {fontSize: '44px', color: COLORS.pink, stroke: COLORS.pink, strokeThickness: 2}))
            .setOrigin(0.5).setAngle(-14).setAlpha(0);
        layer.add(stamp);

        const cycle = () => {
            rows.forEach(r => {
                r.box.setFillStyle(HEX.bgDeep);
                r.tick.setVisible(false);
                r.line.setScale(0, 1);
            });
            stamp.setAlpha(0).setScale(2);
            rows.forEach((r, i) => run.after(400 + i * 800, () => {
                run.tween({targets: r.line, scaleX: 1, duration: 450});
                r.box.setFillStyle(HEX.green);
                r.tick.setVisible(true);
            }));
            run.after(2900, () => run.tween({targets: stamp, alpha: 1, scale: 1, duration: 260, ease: 'Back.easeOut'}));
        };
        cycle();
        run.every(5000, cycle);
        return () => run.stop();
    },

    // Residents chatting: speech bubbles pop up one after another, and one of them sounds a little off.
    chatter(scene, layer, {cx, cy}) {
        const run = runner(scene);
        const xs = [cx - 360, cx - 120, cx + 120, cx + 360];
        const residents = [1, 3, 0, 5].map((k, i) => sprite(scene, layer, CHARACTER_KEYS[k], xs[i], cy + 50, 140));
        residents.forEach((r, i) => bob(run, r, i, 6));

        const lines = ['hi everyone!', 'lol same', 'cats or dogs?', 'beep. I mean: hi!', 'pizza > tacos', 'hmm, sus…'];
        const speakers = [0, 2, 1, 3, 2, 0];
        const shown = [];
        let i = 0;
        const say = () => {
            const n = i % lines.length;
            const x = xs[speakers[n]];
            const b = bubble(scene, layer, x, cy - 80 - (i % 2) * 50, lines[n]).setScale(0);
            run.tween({targets: b, scale: 1, duration: 220, ease: 'Back.easeOut'});
            shown.push(b);
            if (shown.length > 2) {
                const old = shown.shift();
                scene.tweens.killTweensOf(old);
                old.destroy();
            }
            i++;
        };
        say();
        run.every(1100, say);
        return () => {
            run.stop();
            scene.tweens.killTweensOf(shown);
        };
    },

    // The vote: a marker hops between residents, settles on one, and they turn out to be a bot.
    vote(scene, layer, {cx, cy}) {
        const run = runner(scene);
        const xs = [cx - 330, cx - 110, cx + 110, cx + 330];
        const keys = [CHARACTER_KEYS[0], CHARACTER_KEYS[2], CHARACTER_KEYS[3], CHARACTER_KEYS[5]];
        const residents = keys.map((key, i) => sprite(scene, layer, key, xs[i], cy + 30, 150));
        const marker = scene.add.triangle(xs[0], cy - 90, 0, 0, 44, 0, 22, 34, HEX.gold);
        const label = scene.add.text(xs[2], cy - 100, 'BOT!', textStyle('title', {fontSize: '48px', color: COLORS.pink})).setOrigin(0.5).setAlpha(0);
        layer.add([marker, label]);

        const target = 2;
        const hops = [0, 1, 2, 3, 1, target];
        const cycle = () => {
            const victim = residents[target];
            victim.setTexture(keys[target]).setDisplaySize(150, 150).setY(cy + 30).setAlpha(1);
            label.setAlpha(0);
            hops.forEach((h, n) => run.after(n * 420, () => run.tween({targets: marker, x: xs[h], duration: 200, ease: 'Quad.easeOut'})));
            const settled = hops.length * 420;
            run.after(settled, () => run.tween({targets: victim, y: cy + 90, alpha: 0, duration: 350}));
            run.after(settled + 450, () => {
                victim.setTexture(ROBOT_KEYS[1]).setDisplaySize(150, 150);
                run.tween({targets: victim, y: cy + 30, alpha: 1, duration: 300, ease: 'Back.easeOut'});
                run.tween({targets: label, alpha: 1, duration: 200});
            });
        };
        cycle();
        run.every(hops.length * 420 + 2600, cycle);
        return () => run.stop();
    },

    // The suspicion meter: bars filling up beside the residents, one pulling ahead.
    meter(scene, layer, {cx, cy}) {
        const run = runner(scene);
        const width = 420;
        const bars = [0.3, 0.85, 0.45, 0.2].map((share, i) => {
            const y = cy - 150 + i * 80;
            const face = sprite(scene, layer, CHARACTER_KEYS[i], cx - width / 2 - 60, y, 70);
            const track = scene.add.rectangle(cx - width / 2, y, width, 30, HEX.surfaceRaised).setOrigin(0, 0.5).setStrokeStyle(2, HEX.border);
            const fill = scene.add.rectangle(cx - width / 2, y, width * share, 30, i === 1 ? HEX.pink : HEX.cyan).setOrigin(0, 0.5).setScale(0, 1);
            layer.add([track, fill]);
            return {face, fill};
        });
        const cycle = () => bars.forEach(({fill}, i) => {
            fill.setScale(0, 1);
            run.tween({targets: fill, scaleX: 1, duration: 1100, delay: i * 180, ease: 'Cubic.easeOut'});
        });
        cycle();
        run.every(3200, cycle);
        bob(run, bars[1].face, 0, 8);
        return () => run.stop();
    },

    // The verdict: a spotlight sweeping the line-up under a question mark. It never settles on anyone.
    verdict(scene, layer, {cx, cy}) {
        const run = runner(scene);
        const xs = [cx - 330, cx - 110, cx + 110, cx + 330];
        const light = scene.add.ellipse(xs[0], cy + 30, 190, 220, HEX.gold, 0.28);
        layer.add(light);
        xs.forEach((x, i) => bob(run, sprite(scene, layer, CHARACTER_KEYS[i + 1], x, cy + 30, 150), i, 4));
        const mark = scene.add.text(cx, cy - 130, '?', textStyle('title', {fontSize: '110px', color: COLORS.gold})).setOrigin(0.5);
        layer.add(mark);
        run.tween({targets: light, x: xs[3], duration: 1400, yoyo: true, repeat: -1, ease: 'Sine.easeInOut'});
        run.tween({targets: mark, scale: 1.2, duration: 500, yoyo: true, repeat: -1, ease: 'Sine.easeInOut'});
        return () => run.stop();
    },

    // Humans on one side, bots on the other.
    standoff(scene, layer, {cx, cy}) {
        const run = runner(scene);
        const humans = [0, 1, 4].map((k, i) => sprite(scene, layer, CHARACTER_KEYS[k], cx - 440 + i * 130, cy + 10, 130));
        const bots = ROBOT_KEYS.map((key, i) => sprite(scene, layer, key, cx + 180 + i * 130, cy + 10, 130));
        [...humans, ...bots].forEach((s, i) => bob(run, s, i, 8));

        const vs = scene.add.text(cx, cy + 10, 'VS', textStyle('title', {fontSize: '80px', color: COLORS.gold})).setOrigin(0.5);
        const humansLabel = scene.add.text(cx - 310, cy + 110, 'HUMANS', textStyle('meta', {fontSize: '24px'})).setOrigin(0.5);
        const botsLabel = scene.add.text(cx + 310, cy + 110, 'BOTS', textStyle('meta', {fontSize: '24px', color: COLORS.pink})).setOrigin(0.5);
        layer.add([vs, humansLabel, botsLabel]);
        run.tween({targets: vs, scale: 1.15, duration: 600, yoyo: true, repeat: -1, ease: 'Sine.easeInOut'});
        return () => run.stop();
    },
};
