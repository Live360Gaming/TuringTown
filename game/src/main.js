import { Boot } from './scenes/Boot';
import { Preloader } from "./scenes/Preloader.js";
import { Game } from 'phaser';
import { MainMenu } from "./scenes/MainMenu.js";
import * as Phaser from 'phaser';
import {PHONE_HEIGHT, PHONE_WIDTH} from "./constants.js";
import {Tv} from "./scenes/Tv.js";

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
