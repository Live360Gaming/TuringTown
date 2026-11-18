import {Scene} from 'phaser'
import {PLAYER_WIDTH, PLAYER_HEIGHT, HOST_WIDTH, HOST_HEIGHT, PLAYER_CENTER_X, PLAYER_CENTER_Y} from "../../constants.js";

export class MainMenu extends Scene {
    constructor() {
        super('MainMenu');
    }

    create() {
        const buttonHeight = 170;

        this.add.image(PLAYER_CENTER_X, PLAYER_CENTER_Y, 'background');

        // Create the Host button
        const hostBtn = this.createButton(PLAYER_CENTER_X, PLAYER_CENTER_Y - buttonHeight / 2, 'start_tv_btn', () => {
            this.scene.start('Host');
        });

        // Create the Player Join button
        const playerBtn = this.createButton(PLAYER_CENTER_X, PLAYER_CENTER_Y + buttonHeight / 2, 'join_phone_btn', () => {
            this.scene.start('Player');
        });
    }

    createButton(x, y, textureKey, onClick) {
        const btn = this.add.image(x, y, textureKey)
            .setInteractive({useHandCursor: true});

        // Hover effect (desktop)
        btn.on('pointerover', () => {
            btn.setScale(1.05);
            btn.setTint(0xffffff);
        });

        btn.on('pointerout', () => {
            btn.setScale(1);
            btn.clearTint();
        });

        // Press down effect
        btn.on('pointerdown', () => {
            btn.setScale(0.96);
            btn.setTint(0xcccccc);
        });

        // Trigger action on release
        btn.on('pointerup', () => {
            btn.setScale(1.05);
            btn.clearTint();
            onClick();
        });

        return btn;
    }
}
