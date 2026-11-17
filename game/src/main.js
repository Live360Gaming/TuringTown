import { Boot } from './scenes/Boot';
import { Preloader } from "./scenes/Preloader.js";
import { Game } from 'phaser';
import * as Phaser from 'phaser';

const config = {
    type: Phaser.AUTO,
    width: 800,
    height: 600,
    parent: 'game-container',
    backgroundColor: 'black',
    scale: {
        mode: Phaser.Scale.RESIZE,
        autoCenter: Phaser.Scale.FIT
    },
    scene: [
        Boot,
        Preloader,
    ]
};

export default new Game(config);
