import * as Phaser from 'phaser';
import {Scene} from 'phaser';
import {enterFullWindow} from '../scaling.js';
import {createTextButton} from '../ui/Button.js';
import {drawPanel} from '../ui/Panel.js';
import {listen} from './listen.js';
import {getSession, leaveToMenu} from '../session.js';
import {attachCountdown} from '../countdown.js';
import {createAvatar} from '../characters.js';

/**
 * Base for a screen on a player's phone or laptop during a game. It fills the whole window and draws the card
 * every screen sits on, with the player's own avatar and alias at the top, the stage countdown and a small
 * Quit button. Subclasses create their objects in `build()` and position them in `layout(m)`.
 *
 * `layout` gets the metrics `m`: {cx, cy, s, width, height, panelW, panelH, left, right, top, bottom}, where `s`
 * is the scale factor for this window size and the rest describe the card.
 */
export class PlayerStage extends Scene {
    constructor(key, {escapeLeaves = true} = {}) {
        super(key);
        // False on screens with a text box, where typing ESC should not drop you out of the game.
        this.escapeLeaves = escapeLeaves;
    }

    create() {
        enterFullWindow(this.scale);

        this.session = getSession(this);
        if (this.session?.role !== 'player') {
            this.scene.start('MainMenu');
            return;
        }

        this.background = this.add.image(0, 0, 'background');
        this.panel = this.add.graphics();

        const you = this.session.you;
        this.youAvatar = you ? createAvatar(this, 0, 0, 72, you) : null;
        this.youText = this.add.text(0, 0, you?.alias ?? '', {color: you?.color ?? '#ffffff'})
            .setOrigin(0, 0.5).setResolution(window.devicePixelRatio);
        this.timerText = this.add.text(0, 0, '', {color: '#ffffff'})
            .setOrigin(1, 0.5).setResolution(window.devicePixelRatio);
        this.quitBtn = createTextButton(this, 0, 0, 'Quit', () => leaveToMenu(this), {width: 110, height: 44, fontSize: '20px'});

        if (this.escapeLeaves) this.input.keyboard.on('keydown-ESC', () => leaveToMenu(this));
        attachCountdown(this, this.session, this.timerText);

        this.build();
        this.relayout();
        this.scale.on('resize', this.relayout, this);
        this.events.once('shutdown', () => this.scale.off('resize', this.relayout, this));
    }

    /** Create this screen's objects. */
    build() {}

    /** Position this screen's objects. Called on every resize. */
    layout() {}

    relayout() {
        const {width, height} = this.scale;
        const cx = width / 2;
        const cy = height / 2;
        const s = Phaser.Math.Clamp(Math.min(width, height) / 720, 0.5, 1.5);
        const panelW = 560 * s;
        const panelH = 700 * s;
        const m = {
            cx, cy, s, width, height, panelW, panelH,
            left: cx - panelW / 2, right: cx + panelW / 2, top: cy - panelH / 2, bottom: cy + panelH / 2,
        };

        this.background.setPosition(cx, cy)
            .setScale(Math.max(width / this.background.width, height / this.background.height));

        drawPanel(this.panel, m.left, m.top, panelW, panelH, s);

        this.youAvatar?.setPosition(m.left + 54 * s, m.top + 54 * s).setScale(s * 0.8);
        this.youText.setPosition(m.left + 100 * s, m.top + 54 * s).setFontSize(26 * s);
        this.timerText.setPosition(m.right - 24 * s, m.top + 54 * s).setFontSize(32 * s);

        const quitScale = Math.min(1, s);
        this.quitBtn.setPosition(width - 62 * quitScale, 32 * quitScale).setScale(quitScale);

        this.layout(m);
    }

    /** Subscribe to an emitter for as long as this scene is running. */
    listen(emitter, event, handler) {
        listen(this, emitter, event, handler);
    }

    /** A text object that stays sharp on high-DPI phones. */
    addText(text = '', style = {}) {
        return this.add.text(0, 0, text, {color: '#ffffff', ...style}).setOrigin(0.5).setResolution(window.devicePixelRatio);
    }
}
