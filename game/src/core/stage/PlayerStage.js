import * as Phaser from 'phaser';
import {Scene} from 'phaser';
import {enterFullWindow, safeInsets} from '../scaling.js';
import {createTextButton} from '../ui/Button.js';
import {drawPanel} from '../ui/Panel.js';
import {listen} from './listen.js';
import {getSession, leaveToMenu} from '../session.js';
import {attachCountdown} from '../countdown.js';
import {createAvatar} from '../characters.js';
import {COLORS, HEX, fontPx, textStyle} from '../theme.js';

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
        this.youRing = this.add.graphics();

        const you = this.session.you;
        this.youAvatar = you ? createAvatar(this, 0, 0, 72, you) : null;
        this.youText = this.add.text(0, 0, you?.alias ?? '', textStyle('name', {color: COLORS.text}))
            .setOrigin(0, 0.5).setResolution(window.devicePixelRatio);
        this.timerText = this.add.text(0, 0, '', textStyle('timer', {stroke: undefined, strokeThickness: 0}))
            .setOrigin(1, 0.5).setResolution(window.devicePixelRatio);
        this.quitBtn = createTextButton(this, 0, 0, 'Quit', () => leaveToMenu(this), {width: 84, height: 44, fontSize: '20px', variant: 'ghost'});

        if (this.escapeLeaves) this.input.keyboard.on('keydown-ESC', () => leaveToMenu(this));
        attachCountdown(this, this.session, this.timerText);

        this.build();
        this.buildSecret();
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
        // The card fills the screen (inside a gutter and the notch margins) instead of a fixed 560x700 shape.
        // `s` is the card's width relative to a 560px card, so subclass geometry still scales as before.
        const safe = safeInsets();
        const gutter = 16;
        const panelW = Math.min(width - 2 * gutter - safe.left - safe.right, 600);
        const panelH = Math.min(height - 2 * gutter - safe.top - safe.bottom, 920);
        const s = Phaser.Math.Clamp(panelW / 560, 0.5, 1.5);
        const cy2 = safe.top + gutter + (height - safe.top - safe.bottom - 2 * gutter) / 2;
        const headerH = Math.max(60, 90 * s);
        const m = {
            cx, cy: cy2, s, width, height, panelW, panelH, safe, headerH,
            left: cx - panelW / 2, right: cx + panelW / 2, top: cy2 - panelH / 2, bottom: cy2 + panelH / 2,
            fs: (px, min = 14) => fontPx(px, s, min),
        };

        this.background.setPosition(cx, cy)
            .setScale(Math.max(width / this.background.width, height / this.background.height));

        drawPanel(this.panel, m.left, m.top, panelW, panelH, s, {headerH});

        // Header: portrait with a ring in the player's color, the alias, the countdown and Quit.
        const hy = m.top + headerH / 2;
        const avatarSize = Math.min(56, headerH - 12);
        const ax = m.left + 14 + avatarSize / 2;
        this.youAvatar?.setPosition(ax, hy).setScale(avatarSize / 72);
        this.youRing.clear();
        if (this.session.you) {
            this.youRing.lineStyle(3, Phaser.Display.Color.HexStringToColor(this.session.you.color).color, 1)
                .strokeCircle(ax, hy, avatarSize / 2 + 1);
        }
        const quitX = m.right - 12 - 42;
        this.quitBtn.setPosition(quitX, hy);
        this.timerText.setPosition(quitX - 42 - 10, hy).setFontSize(m.fs(32, 22));
        const nameLeft = ax + avatarSize / 2 + 12;
        this.fitText(this.youText, m.fs(26, 16), this.timerText.x - this.timerText.width - 10 - nameLeft);
        this.youText.setPosition(nameLeft, hy);

        this.layout(m);
        this.layoutSecret(m);
    }

    /**
     * Turing Town's Imposter: a card the first time this phone learns it (only this phone ever does), then a small gold
     * reminder at the bottom of every Turing Town screen. Nothing appears on anyone else's phone.
     */
    buildSecret() {
        this.secretPill = this.addText('', {fontStyle: 'bold'}).setColor(COLORS.gold).setDepth(30);
        this.secretCard = null;
        this.listen(this.session, 'secret', () => {
            this.showSecret();
            this.relayout();
        });
        this.showSecret();
    }

    showSecret() {
        const secret = this.session.secret;
        const here = secret && this.session.state?.gameId === 'turing-town' && this.session.state?.stage !== 'Results';
        this.secretPill.setText(here ? `★ ${secret.title}` : '').setVisible(!!here && this.session.secretSeen);
        if (!here || this.session.secretSeen || this.secretCard) return;

        const shade = this.add.rectangle(0, 0, 10, 10, HEX.bgDeep, 0.85).setOrigin(0).setInteractive().setDepth(40);
        const plate = this.add.graphics().setDepth(41);
        const title = this.addText(secret.title, {fontStyle: 'bold', align: 'center'}).setColor(COLORS.gold).setDepth(42);
        const text = this.addText(secret.text, {align: 'center'}).setDepth(42);
        const ok = createTextButton(this, 0, 0, 'Got it', () => {
            this.session.secretSeen = true;
            [shade, plate, title, text, ok].forEach(item => item.destroy());
            this.secretCard = null;
            this.showSecret();
            this.relayout();
        }, {width: 240, height: 64, fontSize: '28px', variant: 'primary'}).setDepth(42);
        this.secretCard = {shade, plate, title, text, ok};
    }

    layoutSecret(m) {
        this.secretPill?.setPosition(m.cx, m.bottom - 22 * m.s).setFontSize(m.fs(18, 13));
        const card = this.secretCard;
        if (!card) return;
        const w = m.panelW - 30 * m.s;
        card.shade.setSize(m.width, m.height);
        card.title.setFontSize(m.fs(38, 22)).setWordWrapWidth(w - 40 * m.s);
        card.text.setFontSize(m.fs(24, 15)).setWordWrapWidth(w - 50 * m.s);
        const h = card.title.height + card.text.height + 160 * m.s;
        const top = m.cy - h / 2;
        card.plate.clear().fillStyle(HEX.surface, 1).fillRoundedRect(m.cx - w / 2, top, w, h, 22)
            .lineStyle(4, HEX.gold, 1).strokeRoundedRect(m.cx - w / 2, top, w, h, 22);
        card.title.setPosition(m.cx, top + 30 * m.s + card.title.height / 2);
        card.text.setPosition(m.cx, card.title.y + card.title.height / 2 + 20 * m.s + card.text.height / 2);
        card.ok.setPosition(m.cx, top + h - 50 * m.s).setScale(Math.max(m.s, 0.6));
    }

    /** Shrinks a single-line text to fit `maxWidth`, down to 13px, then cuts it with an ellipsis. */
    fitText(text, fontSize, maxWidth) {
        const full = text.getData('full') ?? text.text;
        text.setData('full', full).setText(full);
        for (let size = fontSize; size >= 13; size--) {
            text.setFontSize(size);
            if (text.width <= maxWidth) return;
        }
        let cut = full;
        while (text.width > maxWidth && cut.length > 1) {
            cut = cut.slice(0, -1);
            text.setText(`${cut.trimEnd()}…`);
        }
    }

    /** Subscribe to an emitter for as long as this scene is running. */
    listen(emitter, event, handler) {
        listen(this, emitter, event, handler);
    }

    /** A text object that stays sharp on high-DPI phones. */
    addText(text = '', style = {}) {
        return this.add.text(0, 0, text, textStyle('body', {color: COLORS.text, ...style})).setOrigin(0.5).setResolution(window.devicePixelRatio);
    }
}
