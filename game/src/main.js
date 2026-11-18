import { Boot } from './scenes/shared/Boot.js';
import { Preloader } from "./scenes/shared/Preloader.js";
import { Game } from 'phaser';
import { MainMenu } from "./scenes/shared/MainMenu.js";
import * as Phaser from 'phaser';
import {PLAYER_HEIGHT, PLAYER_WIDTH} from "./constants.js";
import {Host} from "./scenes/host/Host.js";
import {Player} from "./scenes/player/Player.js";

const config = {
    type: Phaser.AUTO,
    // Start with PLAYER size
    width: PLAYER_WIDTH,
    height: PLAYER_HEIGHT,
    parent: 'game-container',
    // Lets scenes use real HTML elements (e.g. the room code input)
    dom: {createContainer: true},
    backgroundColor: 'black',
    // Boot, loading and menu screens fill the whole window; Host switches to a fixed 16:9 FIT.
    scale: {
        mode: Phaser.Scale.RESIZE,
        autoCenter: Phaser.Scale.CENTER_BOTH
    },
    scene: [
        Boot,
        Preloader,
        MainMenu,
        Host,
        Player,
    ]
};

export default new Game(config);
