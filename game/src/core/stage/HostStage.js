import {Scene} from 'phaser';
import {HOST_CENTER_X, HOST_HEIGHT, HOST_WIDTH} from '../../constants.js';
import {enterFixedSize, fitImageToScreen} from '../scaling.js';
import {getSession, leaveToMenu} from '../session.js';
import {attachCountdown} from '../countdown.js';
import {createAvatar} from '../characters.js';
import {listen} from './listen.js';
import {COLORS, HEX, textStyle} from '../theme.js';
import {addNameplate} from '../ui/Nameplate.js';
import {getNarration} from '../narration/index.js';
import {speechOccurrence} from '../narration/scopes.js';
import {narrationEntry} from '../../narration/registry.js';

/**
 * Base for a screen on the TV during a game: the fixed 16:9 canvas, background, a title, the stage countdown,
 * and ESC to leave. Subclasses fill in `build()`. The scene reads everything from `this.session`.
 */
export class HostStage extends Scene {
    create() {
        enterFixedSize(this.scale, HOST_WIDTH, HOST_HEIGHT);

        this.session = getSession(this);
        if (this.session?.role !== 'host') {
            this.scene.start('MainMenu');
            return;
        }

        fitImageToScreen(this, 'background_tv');
        this.header = this.add.graphics();
        this.title = this.add.text(HOST_CENTER_X, 48, '', textStyle('title', {fontSize: '44px'})).setOrigin(0.5);
        this.timerText = this.add.text(HOST_WIDTH - 40, 48, '', textStyle('timer', {fontSize: '40px'})).setOrigin(1, 0.5);
        // Optional extras inside the header plate; they stay hidden unless a scene uses them.
        this.instructionText = this.add.text(HOST_CENTER_X, 100, '', textStyle('instruction')).setOrigin(0.5);
        this.headerProgressText = this.add.text(72, 34, '', textStyle('meta')).setOrigin(0, 0.5).setVisible(false);
        this.headerProgressBar = this.add.graphics();
        this.headerProgress = null;
        this.drawHeader();

        attachCountdown(this, this.session, this.timerText);
        this.time.addEvent({delay: 250, loop: true, callback: () => this.tintTimer()});

        this.input.keyboard.on('keydown-ESC', () => leaveToMenu(this));

        this.build();
    }

    /** Create this screen's objects. */
    build() {}

    setTitle(text) {
        this.title.setText(text);
    }

    /** A line under the title telling the room what to do, e.g. "Answer the questions on your phone". */
    setInstruction(text) {
        this.instructionText.setText(text);
        this.drawHeader();
    }

    /** "3 of 5 done" with a bar, in the header. Pass total 0 to hide it. */
    setProgress(done, total) {
        this.headerProgress = total > 0 ? {done, total} : null;
        this.headerProgressText.setVisible(this.headerProgress !== null).setText(this.headerProgress ? `${done} / ${total}` : '');
        this.drawHeader();
    }

    /** The dark plate behind the title, timer and instruction. It stays in the ceiling band, above the sign. */
    drawHeader() {
        const height = this.instructionText.text ? 120 : 76;
        this.header.clear()
            .fillStyle(HEX.bgDeep, 0.82).fillRoundedRect(40, 10, HOST_WIDTH - 80, height, 20)
            .lineStyle(2, HEX.cyan, 0.7).strokeRoundedRect(40, 10, HOST_WIDTH - 80, height, 20);
        this.instructionText.setVisible(this.instructionText.text !== '');

        this.headerProgressBar.clear();
        if (this.headerProgress) {
            const {done, total} = this.headerProgress;
            this.headerProgressBar
                .fillStyle(HEX.surfaceRaised, 1).fillRoundedRect(72, 54, 200, 10, 5)
                .fillStyle(HEX.green, 1).fillRoundedRect(72, 54, Math.max(10, 200 * Math.min(1, done / total)), 10, 5);
        }
    }

    /** The timer turns gold with 30s left and pink with 10s left. */
    tintTimer() {
        const ms = this.session.msLeft();
        const color = ms === null || ms > 30000 ? COLORS.text : ms > 10000 ? COLORS.gold : COLORS.pink;
        this.timerText.setColor(color);
    }

    /**
     * Says a recorded line over this screen, with its words in a caption across the bottom, and the AI host's
     * `hostLine` (if any) under them in gold. The caption goes once the line ends, or after the line's length when it
     * cannot be played. A line is said once per round, however often the screen is drawn.
     */
    speak(key, hostLine) {
        const entry = narrationEntry(key);
        const height = hostLine ? 118 : 86;
        const top = HOST_HEIGHT - height - 18;
        const plate = this.add.graphics().setDepth(20)
            .fillStyle(HEX.bgDeep, 0.92).fillRoundedRect(70, top, HOST_WIDTH - 140, height, 18)
            .lineStyle(2, HEX.cyan, 0.7).strokeRoundedRect(70, top, HOST_WIDTH - 140, height, 18);
        const caption = this.add.text(HOST_CENTER_X, top + 40, entry.transcript, textStyle('instruction', {
            fontSize: '24px', align: 'center', wordWrap: {width: HOST_WIDTH - 200},
        })).setOrigin(0.5).setDepth(21);
        const items = [plate, caption];
        if (hostLine) {
            items.push(this.add.text(HOST_CENTER_X, top + height - 24, hostLine, textStyle('instruction', {fontSize: '22px', color: COLORS.gold}))
                .setFontStyle('italic').setOrigin(0.5).setDepth(21));
        }

        const hide = () => items.forEach(item => item.active && item.destroy());
        getNarration(this.game).play(key, {occurrence: speechOccurrence(key, this.session.state.round)}).then(result => {
            if (!plate.active) return;
            if (result === 'missing' || result === 'blocked') this.time.delayedCall(entry.durationMs, hide);
            else hide();
        });
    }

    /** Subscribe to an emitter for as long as this scene is running. */
    listen(emitter, event, handler) {
        listen(this, emitter, event, handler);
    }

    /**
     * A row of everyone's avatar and alias, kept up to date as players come, go or are voted out.
     * Returns a function that redraws it.
     */
    createRosterRow(y, {size = null, filter = () => true} = {}) {
        let items = [];
        const draw = () => {
            items.forEach(item => item.destroy());
            items = [];

            const players = this.session.roster.filter(filter);
            const gap = Math.min(170, (HOST_WIDTH - 120) / Math.max(players.length, 1));
            const startX = HOST_CENTER_X - ((players.length - 1) * gap) / 2;

            const avatarSize = size ?? Math.min(150, gap * 0.9);
            players.forEach((player, i) => {
                const x = startX + i * gap;
                items.push(createAvatar(this, x, y, avatarSize, player, {dim: !player.alive}));
                items.push(addNameplate(this, x, y + avatarSize / 2 + 22, player, {maxWidth: gap - 8, fontSize: 20})
                    .setAlpha(player.alive ? 1 : 0.4));
            });
        };

        this.listen(this.session, 'roster', draw);
        draw();
        return draw;
    }
}
