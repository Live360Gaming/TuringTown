import {Scene} from 'phaser'
import {enterFullWindow, fitImageToScreen} from "../../core/scaling.js";
import {createImageButton} from "../../core/ui/Button.js";
import {MENU_NOTICE_KEY} from "../../core/session.js";

export class MainMenu extends Scene {
    constructor() {
        super('MainMenu');
    }

    create() {
        // The menu fills the whole window (Host switches to a fixed 16:9 size when it starts).
        enterFullWindow(this.scale);

        fitImageToScreen(this, 'background');

        // A season's button art can have another size; scale each button so it is as wide as the normal one.
        const BUTTON_WIDTH = 432;
        const baseScale = (btn) => () => this.baseScale * BUTTON_WIDTH / btn.width;

        // Create the Host button
        this.hostBtn = createImageButton(this, 'start_tv_btn', () => {
            this.scene.start('Host');
        }, () => baseScale(this.hostBtn)());

        // Create the Player Join button
        this.playerBtn = createImageButton(this, 'join_phone_btn', () => {
            this.scene.start('Player');
        }, () => baseScale(this.playerBtn)());

        // One-time message, e.g. the goodbye when the host closed the room.
        const notice = this.game.registry.get(MENU_NOTICE_KEY);
        this.game.registry.remove(MENU_NOTICE_KEY);
        this.notice = notice
            ? this.add.text(0, 0, notice, {fontSize: '24px', color: '#ffffff', align: 'center', backgroundColor: '#000000cc', padding: {x: 16, y: 12}}).setOrigin(0.5, 0)
            : null;

        this.layout();
        this.scale.on('resize', this.layout, this);
        this.events.once('shutdown', () => this.scale.off('resize', this.layout, this));
    }

    /** Center the buttons in the current window size. */
    layout() {
        const {width, height} = this.scale;
        const buttonHeight = 170;
        const buttonScale = (btn) => this.baseScale * 432 / btn.width;
        // Shrink the buttons on small screens so they always fit.
        this.baseScale = Math.min(1, width / 720, height / 600);

        this.hostBtn.setPosition(width / 2, height / 2 - (buttonHeight / 2) * this.baseScale);
        this.playerBtn.setPosition(width / 2, height / 2 + (buttonHeight / 2) * this.baseScale);
        this.hostBtn.setScale(buttonScale(this.hostBtn));
        this.playerBtn.setScale(buttonScale(this.playerBtn));
        this.notice?.setPosition(width / 2, 16).setWordWrapWidth(width - 32);
    }
}
