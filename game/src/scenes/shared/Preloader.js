import * as Phaser from 'phaser';
import { Scene } from 'phaser';
import { fitImageToScreen } from '../../core/fitImage.js';

// Loading screen to download all the assets before starting the game.
export class Preloader extends Scene
{
    constructor ()
    {
        super('Preloader');
    }

    init ()
    {
        //  We loaded this image in our Boot Scene, so we can display it here
        fitImageToScreen(this, 'preloader');
    }

    preload ()
    {
        //  Load the assets for the game - Replace with the path to your own assets
        this.load.setPath('assets');

        this.load.image('join_phone_btn', 'join_phone_btn.jpg');
        this.load.image('start_tv_btn', 'start_tv_btn.jpg');
        this.load.image('background', 'background.jpg');
        this.load.image('background_tv', 'background_tv.jpg');

        this.load.setPath('audio');
        this.load.audio('lobby_music', 'lobby_music.mp3');
    }

    create ()
    {
        // TEMP: random delay (1-3s) so the loading screen is visible.
        const delay = Phaser.Math.Between(1000, 3000);
        this.time.delayedCall(delay, () => this.scene.start('MainMenu'));
    }
}
