import { Boot } from './scenes/shared/Boot.js';
import { Preloader } from "./scenes/shared/Preloader.js";
import { Game } from 'phaser';
import { MainMenu } from "./scenes/shared/MainMenu.js";
import * as Phaser from 'phaser';
import {PHONE_HEIGHT, PHONE_WIDTH} from "./constants.js";
import {Tv} from "./scenes/tv/Tv.js";

const config = {
    type: Phaser.AUTO,
    // Start with PHONE size
    width: PHONE_WIDTH,
    height: PHONE_HEIGHT,
    parent: 'game-container',
    backgroundColor: 'black',
    scale: {
        mode: Phaser.Scale.FIT,
        autoCenter: Phaser.Scale.CENTER_BOTH
    },
    scene: [
        Boot,
        Preloader,
        MainMenu,
        Tv,
    ]
};

export default new Game(config);
