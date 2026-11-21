import {HOST_CENTER_X, HOST_CENTER_Y, HOST_WIDTH} from '../../constants.js';
import {ensurePlaceholderImage} from '../placeholders.js';
import {TUTORIAL_ANIMATIONS} from './animations.js';
import {getMusic} from '../music.js';
import {COLORS, HEX, textStyle} from '../theme.js';

/**
 * Plays a game's how-to-play on the TV, one step after another.
 *
 * A step is plain data (see a game's tutorial data file):
 *   {id, type: 'animation' | 'slide' | 'video', title?, caption, durationMs, narration?, animation?, image?, video?}
 *  - animation: a named animation from ./animations.js
 *  - slide:     a still image (a placeholder is drawn if the real image has not been added yet)
 *  - video:     a video that was loaded under `video`; the step ends when it finishes
 * Every step shows its title (if any) and caption, and can play a narration sound (an audio key). Steps other than video last
 * `durationMs`.
 *
 * `onStep(index)` is called as each step starts, so the room's phones can follow along.
 * `onDone()` is called once after the last step, or when `finish()` is called to skip the rest.
 */
export class TutorialPlayer {
    constructor(scene, steps, {onStep = () => {}, onDone = () => {}} = {}) {
        this.scene = scene;
        this.steps = steps;
        this.onStep = onStep;
        this.onDone = onDone;

        this.layer = scene.add.container(0, 0);
        this.title = scene.add.text(HOST_CENTER_X, 548, '', textStyle('title', {fontSize: '38px', color: COLORS.gold})).setOrigin(0.5);
        this.caption = scene.add.text(HOST_CENTER_X, 612, '', textStyle('instruction', {
            fontSize: '27px', align: 'center', wordWrap: {width: HOST_WIDTH - 260},
        })).setOrigin(0.5, 0.5);

        // One dot per step, the current one lit.
        const gap = 26;
        this.dots = steps.map((_, i) => scene.add.circle(HOST_CENTER_X + (i - (steps.length - 1) / 2) * gap, 504, 6, HEX.border));

        this.index = -1;
        this.finished = false;
        this.timer = null;
        this.stopAnimation = null;
        this.narration = null;
    }

    start(index = 0) {
        this.play(index);
    }

    play(index) {
        if (this.finished) return;
        this.clearStep();

        if (index >= this.steps.length) {
            this.finish();
            return;
        }

        this.index = index;
        const step = this.steps[index];
        this.title.setText(step.title ?? '');
        this.caption.setText(step.caption ?? '');
        this.dots.forEach((dot, i) => dot.setFillStyle(i === index ? HEX.cyan : i < index ? HEX.textDim : HEX.border).setScale(i === index ? 1.3 : 1));
        this.onStep(index);

        const area = {cx: HOST_CENTER_X, cy: HOST_CENTER_Y};
        const next = () => this.play(index + 1);

        if (step.type === 'video' && this.scene.cache.video.exists(step.video)) {
            const video = this.scene.add.video(area.cx, area.cy - 40, step.video);
            this.layer.add(video);
            video.once('complete', next);
            video.play();
        } else {
            this.showStill(step, area);
            this.timer = this.scene.time.delayedCall(step.durationMs ?? 5000, next);
        }

        if (step.narration && this.scene.cache.audio.exists(step.narration)) {
            this.narration = this.scene.sound.add(step.narration);
            // The music dips under the voice and comes back when it ends.
            getMusic(this.scene.game).duck(true);
            this.narration.once('complete', () => getMusic(this.scene.game).duck(false));
            this.narration.play();
        }
    }

    showStill(step, area) {
        if (step.type === 'animation' && TUTORIAL_ANIMATIONS[step.animation]) {
            this.stopAnimation = TUTORIAL_ANIMATIONS[step.animation](this.scene, this.layer, area);
            return;
        }

        // A slide, or a video/animation we cannot play: show an image so the step is never blank.
        const key = step.image ?? `tutorial_${step.id}`;
        ensurePlaceholderImage(this.scene, key, {width: 640, height: 360, label: step.id});
        this.layer.add(this.scene.add.image(area.cx, area.cy - 40, key));
    }

    /** Stops what the current step started. */
    clearStep() {
        this.timer?.remove();
        this.timer = null;
        this.stopAnimation?.();
        this.stopAnimation = null;
        if (this.narration) getMusic(this.scene.game).duck(false);
        this.narration?.destroy();
        this.narration = null;
        this.layer.removeAll(true);
    }

    /** Ends the tutorial now (the host skipped it, or the last step ended). */
    finish() {
        if (this.finished) return;
        this.finished = true;
        this.clearStep();
        this.onDone();
    }

    destroy() {
        this.finished = true;
        this.clearStep();
    }
}
