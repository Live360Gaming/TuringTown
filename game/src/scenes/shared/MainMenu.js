import {Scene} from 'phaser'
import {fitImageToScreen} from "../../core/fitImage.js";
import {PLAYER_WIDTH, PLAYER_HEIGHT, HOST_WIDTH, HOST_HEIGHT, PLAYER_CENTER_X, PLAYER_CENTER_Y} from "../../constants.js";

export class MainMenu extends Scene {
    constructor() {
        super('MainMenu');
    }

    create() {
        fitImageToScreen(this, 'background');

        // Create the Host button
        this.hostBtn = this.createButton('start_tv_btn', () => {
            this.scene.start('Host');
        });

        // Create the Player Join button
        this.playerBtn = this.createButton('join_phone_btn', () => {
            this.scene.start('Player');
        });

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

    createButton(textureKey, onClick) {
        const btn = this.add.image(0, 0, textureKey)
            .setInteractive({useHandCursor: true});

        // Hover effect (desktop)
        btn.on('pointerover', () => {
            btn.setScale(1.05 * this.baseScale);
            btn.setTint(0xffffff);
        });

        btn.on('pointerout', () => {
            btn.setScale(this.baseScale);
            btn.clearTint();
        });

        // Press down effect
        btn.on('pointerdown', () => {
            btn.setScale(0.96 * this.baseScale);
            btn.setTint(0xcccccc);
        });

        // Trigger action on release
        btn.on('pointerup', () => {
            btn.setScale(1.05 * this.baseScale);
            btn.clearTint();
            onClick();
        });

        return btn;
    }
}
