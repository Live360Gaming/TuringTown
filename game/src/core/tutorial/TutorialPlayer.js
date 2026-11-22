import {HOST_CENTER_X, HOST_CENTER_Y, HOST_WIDTH} from '../../constants.js';
import {ensurePlaceholderImage} from '../placeholders.js';
import {TUTORIAL_ANIMATIONS} from './animations.js';
import {AUDIO_MIX, getMusic} from '../music.js';
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
 * `durationMs`, a minimum: a step with narration also waits for the line to end, plus a short pause (AUDIO_MIX in
 * core/music.js), so the voice is never cut off. A clip that is missing, failed to load or never reports its end falls
 * back to the visual timing. Only one line plays at a time. The music is ducked from the first line until the tutorial
 * ends or is skipped, rather than bobbing up and down between lines.
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
        /** The current step's timers (its minimum time, the pause after the voice, the voice's safety limit). */
        this.timers = [];
        this.stopAnimation = null;
        this.narration = null;
        /** This player's claim on the music duck, so another player letting go cannot lift it. */
        this.duckOwner = Symbol('tutorial');
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

        // The step moves on once its visuals have had their time and its voice (if any) has finished.
        let visualsDone = false;
        let voiceDone = true;
        const advance = () => {
            if (visualsDone && voiceDone && this.index === index) this.play(index + 1);
        };
        const visualsEnd = () => {
            visualsDone = true;
            advance();
        };

        if (step.type === 'video' && this.scene.cache.video.exists(step.video)) {
            const video = this.scene.add.video(area.cx, area.cy - 40, step.video);
            this.layer.add(video);
            video.once('complete', visualsEnd);
            video.play();
        } else {
            this.showStill(step, area);
            this.later(step.durationMs ?? 5000, visualsEnd);
        }

        if (this.speak(step.narration, () => {
            voiceDone = true;
            advance();
        })) voiceDone = false;
    }

    /**
     * Plays a narration line, ducking the music. Calls `onEnd` a short pause after the line ends. Returns false (and
     * never calls `onEnd`) if there is nothing to play, so the step keeps its visual timing.
     */
    speak(key, onEnd) {
        if (!key || !this.scene.cache.audio.exists(key)) return false;

        let sound;
        try {
            sound = this.scene.sound.add(key, {volume: AUDIO_MIX.narrationVolume});
        } catch (err) {
            console.warn(`Narration "${key}" cannot play`, err);
            return false;
        }
        this.narration = sound;
        getMusic(this.scene.game).duck(true, this.duckOwner);

        let ended = false;
        const end = () => {
            if (ended) return;
            ended = true;
            this.later(AUDIO_MIX.narrationGapMs, onEnd);
        };
        sound.once('complete', end);
        // A line that never reports its end (blocked audio, a broken file) must not hold the tutorial up.
        const lengthMs = (sound.duration || 0) * 1000;
        this.later((lengthMs > 0 ? lengthMs : 10000) + 1500, end);
        if (sound.play() === false) end();
        return true;
    }

    /** A timer that belongs to the current step and is cancelled with it. */
    later(ms, callback) {
        this.timers.push(this.scene.time.delayedCall(ms, callback));
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

    /** Stops what the current step started. The music stays ducked: the next step is likely to speak too. */
    clearStep() {
        this.timers.forEach(timer => timer.remove());
        this.timers = [];
        this.stopAnimation?.();
        this.stopAnimation = null;
        this.narration?.stop();
        this.narration?.destroy();
        this.narration = null;
        this.layer.removeAll(true);
    }

    /** Ends the tutorial now (it was skipped, or the last step ended). */
    finish() {
        if (this.finished) return;
        this.stop();
        this.onDone();
    }

    /** Stops everything without reporting the end (the scene is closing, e.g. the leader skipped from a phone). */
    destroy() {
        this.stop();
    }

    stop() {
        this.finished = true;
        this.clearStep();
        getMusic(this.scene.game).duck(false, this.duckOwner);
    }
}
