import {Scene} from 'phaser'
import {enterFullWindow, fitImageToScreen} from "../../core/scaling.js";
import {createImageButton} from "../../core/ui/Button.js";

export class MainMenu extends Scene {
    constructor() {
        super('MainMenu');
    }

    create() {
        // The menu fills the whole window (Host switches to a fixed 16:9 size when it starts).
        enterFullWindow(this.scale);

        fitImageToScreen(this, 'background');

        const baseScale = () => this.baseScale;

        // Create the Host button
        this.hostBtn = createImageButton(this, 'start_tv_btn', () => {
            this.scene.start('Host');
        }, baseScale);

        // Create the Player Join button
        this.playerBtn = createImageButton(this, 'join_phone_btn', () => {
            this.scene.start('Player');
        }, baseScale);

        this.layout();
        this.scale.on('resize', this.layout, this);
        this.events.once('shutdown', () => this.scale.off('resize', this.layout, this));
    }

    /** Center the buttons in the current window size. */
    layout() {
        const {width, height} = this.scale;
        const buttonHeight = 170;
        // Shrink the buttons on small screens so they always fit.
        this.baseScale = Math.min(1, width / 720, height / 600);

        this.hostBtn.setPosition(width / 2, height / 2 - (buttonHeight / 2) * this.baseScale);
        this.playerBtn.setPosition(width / 2, height / 2 + (buttonHeight / 2) * this.baseScale);
        this.hostBtn.setScale(this.baseScale);
        this.playerBtn.setScale(this.baseScale);
    }
}
