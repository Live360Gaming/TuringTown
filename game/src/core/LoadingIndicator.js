import * as Phaser from 'phaser';

const GREEN = 0x39ff14;
const CYAN = 0x00f0ff;
const GREEN_CSS = '#39FF14';
const OFFWHITE_CSS = '#e8ffe8';
const RED_CSS = '#ff4d4d';
const FONT = '"Courier New", Courier, monospace';

const MESSAGES = [
    "Teaching the AI to act normal...",
    "Checking who's actually human...",
    "Downloading suspicious behavior...",
    "Pretending everything is fine...",
    "Recalibrating trust issues...",
    "Counting the humans. Again...",
    "Installing artificial confidence...",
    "Hiding the evidence...",
    "Convincing the bots to blink...",
    "Generating believable excuses...",
    "Locating the town's last brain cell...",
    "Disabling obvious robot behavior...",
    "Trust.exe has stopped responding...",
    "Someone here is definitely lying...",
    "Teaching machines small talk...",
    "Removing suspicious semicolons...",
    "Polishing the conspiracy theories...",
    "Loading perfectly normal humans...",
    "Human verification: inconclusive.",
    "Please ignore the glowing eyes...",
];

const ALT_LABELS = ['THINKING...', 'SCANNING...', 'VERIFYING HUMANITY...', 'PLEASE STAND BY...', 'NOTHING SUSPICIOUS...'];
const GLITCH_CHARS = '#%&@$01<>/\\|?!';
const SEGMENTS = 12;
const HOLD_MS = 2000;

/**
 * Neon "corrupted computer" loading indicator, anchored bottom-right of a scene.
 * Independent of any background art. Owns all its objects, timers and tweens;
 * call destroy() (idempotent) when the scene shuts down.
 *
 * Options: { onTick, onGlitch, onDone } optional sound hooks (no-ops by default).
 */
export class LoadingIndicator {
    constructor(scene, options = {}) {
        this.scene = scene;
        this.hooks = options;
        this.destroyed = false;
        this.state = 'loading'; // loading | waiting | done | failed
        this.angle = 0;
        this.glitch = 0;       // seconds of glitch remaining
        this.glitchSeg = 0;
        this.t = 0;
        this.progress = 0;
        this.queue = [];
        this.last = null;
        this.cycle = null;
        this.glitchTimer = null;
        this.tweens = [];

        const dpr = window.devicePixelRatio || 1;
        const mk = (color) => scene.add.text(0, 0, '', {fontFamily: FONT, color, fontStyle: 'bold'})
            .setResolution(dpr).setOrigin(1, 0.5).setDepth(1000);

        this.gfx = scene.add.graphics().setDepth(1000);
        this.label = mk(GREEN_CSS);
        this.message = mk(OFFWHITE_CSS);
        this.percent = mk(GREEN_CSS);
        this.extra = mk(RED_CSS).setVisible(false);
        this.retry = mk(GREEN_CSS).setVisible(false);
        this.label.setText('LOADING');
        this.percent.setText('0%');

        this.onUpdate = (_time, delta) => this.update(delta / 1000);
        this.onResize = () => this.layout();
        scene.events.on('update', this.onUpdate);
        scene.scale.on('resize', this.onResize);
        this.layout();

        this.scheduleGlitch();
        this.nextMessage();
    }

    layout() {
        const {width, height} = this.scene.scale;
        const u = Math.max(0.45, Math.min(width, height) / 1080);
        this.u = u;
        const margin = Math.round(Math.min(width, height) * 0.04);
        this.radius = 44 * u;
        this.cx = width - margin - this.radius;
        this.cy = height - margin - this.radius;
        const tx = this.cx - this.radius - 20 * u;
        const size = (px) => `${Math.max(11, Math.round(px * u))}px`;
        this.label.setFontSize(size(18)).setPosition(tx, this.cy - 30 * u);
        this.message.setFontSize(size(24)).setPosition(tx, this.cy);
        this.percent.setFontSize(size(16)).setPosition(tx, this.cy + 28 * u);
        this.extra.setFontSize(size(16)).setPosition(tx, this.cy + 28 * u)
            .setWordWrapWidth(Math.max(200, width * 0.4));
        this.retry.setFontSize(size(22)).setPosition(tx, this.cy + 58 * u);
        this.message.setWordWrapWidth(Math.max(200, width * 0.4));
    }

    // ---------- spinner ----------
    update(dt) {
        if (this.destroyed) return;
        this.t += dt;
        // unstable speed: base + slow sines, glitch makes it stutter
        const speed = 2.6 + Math.sin(this.t * 1.3) * 1.1 + Math.sin(this.t * 3.7) * 0.5;
        this.angle += (this.state === 'failed' ? 0 : speed) * dt;
        if (this.glitch > 0) this.glitch -= dt;
        this.draw();
    }

    draw() {
        const g = this.gfx;
        const {cx, cy, radius: r, u} = this;
        g.clear();
        const glitching = this.glitch > 0;
        const color = this.state === 'failed' ? 0xff4d4d : GREEN;
        const step = (Math.PI * 2) / SEGMENTS;
        const gap = step * 0.28;
        for (let i = 0; i < SEGMENTS; i++) {
            // trailing fade clockwise: head is brightest
            const fade = ((i + 1) / SEGMENTS);
            let alpha = 0.2 + 0.8 * fade;
            if (glitching && (i + this.glitchSeg) % 5 === 0) alpha = Math.random() < 0.5 ? 0.05 : 1;
            const a0 = this.angle + i * step;
            const a1 = a0 + step - gap;
            const off = glitching && i === this.glitchSeg ? 5 * u : 0;
            const seg = (rad, width, c, al) => {
                g.lineStyle(width, c, al);
                g.beginPath();
                g.arc(cx + off, cy, rad, a0, a1, false);
                g.strokePath();
            };
            const hi = i % 4 === 0 && this.state !== 'failed';
            const c = hi ? CYAN : color;
            seg(r, 9 * u * 1.9, c, alpha * 0.12);   // glow
            seg(r, 9 * u, c, alpha);
        }
        // inner ring
        g.lineStyle(2 * u, CYAN, glitching ? 0.9 : 0.45);
        g.beginPath();
        g.arc(cx, cy, r * 0.6, -this.angle * 1.5, -this.angle * 1.5 + Math.PI * 1.2, false);
        g.strokePath();
    }

    scheduleGlitch() {
        if (this.destroyed) return;
        this.glitchTimer = this.scene.time.delayedCall(Phaser.Math.Between(1500, 4500), () => {
            this.glitch = Phaser.Math.FloatBetween(0.08, 0.22);
            this.glitchSeg = Phaser.Math.Between(0, SEGMENTS - 1);
            this.hooks.onGlitch?.();
            if (this.state === 'loading') this.flickerLabel();
            this.scheduleGlitch();
        });
    }

    flickerLabel() {
        const orig = this.label.text;
        this.label.setText(this.scramble(orig, 2));
        this.scene.time.delayedCall(90, () => {
            if (!this.destroyed && this.state === 'loading') this.label.setText(this.currentLabel || orig);
        });
    }

    // ---------- text ----------
    scramble(text, n) {
        const chars = text.split('');
        for (let k = 0; k < n; k++) {
            const i = Phaser.Math.Between(0, chars.length - 1);
            if (chars[i] !== ' ') chars[i] = GLITCH_CHARS[Phaser.Math.Between(0, GLITCH_CHARS.length - 1)];
        }
        return chars.join('');
    }

    pickMessage() {
        if (this.queue.length === 0) {
            this.queue = Phaser.Utils.Array.Shuffle(MESSAGES.slice());
            if (this.queue[this.queue.length - 1] === this.last) this.queue.reverse(); // avoid immediate repeat
        }
        this.last = this.queue.pop();
        return this.last;
    }

    nextMessage() {
        if (this.destroyed || this.state !== 'loading') return;
        const text = this.pickMessage();
        this.currentLabel = Math.random() < 0.3 ? Phaser.Utils.Array.GetRandom(ALT_LABELS) : 'LOADING';
        this.label.setText(this.currentLabel);
        this.hooks.onTick?.();

        // typewriter fade-in with a little jitter
        let shown = 0;
        this.message.setAlpha(1).setText('');
        const per = Math.max(15, Math.min(35, 700 / text.length));
        this.cycle = this.scene.time.addEvent({
            delay: per, repeat: text.length - 1,
            callback: () => {
                shown++;
                this.message.setText(text.slice(0, shown));
                if (shown === text.length) this.afterTyped(text);
            },
        });
    }

    afterTyped(text) {
        this.cycle = this.scene.time.delayedCall(HOLD_MS, () => {
            if (this.state !== 'loading') return;
            this.message.setText(this.scramble(text, 2));
            this.hooks.onGlitch?.();
            this.cycle = this.scene.time.delayedCall(110, () => {
                if (this.state !== 'loading') return;
                this.message.setText(text);
                const tw = this.scene.tweens.add({
                    targets: this.message, alpha: 0, duration: 250,
                    onComplete: () => { this.tweens = this.tweens.filter((x) => x !== tw); this.nextMessage(); },
                });
                this.tweens.push(tw);
            });
        });
    }

    stopRotation() {
        this.cycle?.remove(false);
        this.cycle = null;
        this.tweens.forEach((t) => t.remove());
        this.tweens = [];
    }

    // ---------- public API ----------
    /** @param {number} p 0..1 from the loader's real progress event */
    setProgress(p) {
        this.progress = p;
        if (this.state === 'loading') this.percent.setText(`${Math.round(p * 100)}%`);
    }

    /** Assets are done but another real init step is still pending. */
    setWaiting(text = 'WAITING FOR SYSTEMS...') {
        if (this.destroyed || this.state === 'failed' || this.state === 'done') return;
        this.state = 'waiting';
        this.stopRotation();
        this.label.setText('PLEASE STAND BY...');
        this.message.setAlpha(1).setText(text);
        this.percent.setText('100%');
    }

    /** Plays the short completion flash; resolves when finished. */
    complete() {
        return new Promise((resolve) => {
            if (this.destroyed || this.state === 'failed') return resolve();
            this.state = 'done';
            this.stopRotation();
            this.label.setText('VERIFIED');
            this.message.setAlpha(1).setText('WELCOME TO TURING TOWN.').setColor(GREEN_CSS);
            this.percent.setText('100%');
            this.hooks.onDone?.();
            this.glitch = 0.25;
            const tw = this.scene.tweens.add({
                targets: [this.gfx, this.label, this.message, this.percent],
                alpha: {from: 1, to: 0.15}, duration: 120, yoyo: true, repeat: 1,
                onComplete: () => resolve(),
            });
            this.tweens.push(tw);
            this.scene.time.delayedCall(700, resolve); // safety if scene is torn down mid-tween
        });
    }

    /** Error state with optional retry action. */
    fail(detail, onRetry) {
        if (this.destroyed) return;
        this.state = 'failed';
        this.stopRotation();
        this.label.setText('ERROR').setColor(RED_CSS);
        this.message.setAlpha(1).setText('SOMETHING WENT WRONG...').setColor(RED_CSS);
        this.percent.setVisible(false);
        this.extra.setText(detail || '').setVisible(Boolean(detail));
        if (onRetry) {
            this.retry.setText('[ RETRY ]').setVisible(true)
                .setInteractive({useHandCursor: true})
                .once('pointerup', onRetry);
            this.scene.input.keyboard?.once('keydown-ENTER', onRetry);
        }
        this.layout();
    }

    destroy() {
        if (this.destroyed) return;
        this.destroyed = true;
        this.stopRotation();
        this.glitchTimer?.remove(false);
        const {scene} = this;
        scene.events.off('update', this.onUpdate);
        scene.scale.off('resize', this.onResize);
        scene.input.keyboard?.removeAllListeners('keydown-ENTER');
        [this.gfx, this.label, this.message, this.percent, this.extra, this.retry].forEach((o) => o.destroy());
    }
}
