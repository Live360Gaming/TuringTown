import { Boot } from './scenes/boot/Boot.js';
import { Preloader } from "./scenes/boot/Preloader.js";
import { Game } from 'phaser';
import { MainMenu } from "./scenes/menu/MainMenu.js";
import * as Phaser from 'phaser';
import {PLAYER_HEIGHT, PLAYER_WIDTH} from "./constants.js";
import {Host} from "./scenes/host/Host.js";
import {Player} from "./scenes/player/Player.js";
import {HostSettings} from "./scenes/host/overlays/HostSettings.js";
import {QuitConfirm} from "./scenes/host/overlays/QuitConfirm.js";

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
        // Overlays come after Host so they draw on top of it.
        HostSettings,
        QuitConfirm,
    ]
};

export default new Game(config);
