import * as Phaser from 'phaser';
import { Scene } from 'phaser';
import { fitImageToScreen } from '../../core/fitImage.js';
import { LoadingIndicator } from '../../core/LoadingIndicator.js';

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

        this.failed = [];
        this.indicator = new LoadingIndicator(this);
        this.events.once('shutdown', () => {
            this.load.off('progress', this.onProgress);
            this.load.off('loaderror', this.onLoadError);
            this.indicator?.destroy();
            this.indicator = null;
        });
    }

    preload ()
    {
        this.onProgress = (p) => this.indicator?.setProgress(p);
        this.onLoadError = (file) => this.failed.push(file.key);
        this.load.on('progress', this.onProgress);
        this.load.on('loaderror', this.onLoadError);

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
        // The loader has finished by the time create() runs; failed files are reported here.
        if (this.failed.length > 0) {
            this.indicator.fail(`Failed to load: ${this.failed.join(', ')}`, () => this.scene.restart());
            return;
        }

        // Hold the loading screen for a random 3-5s so the messages can be enjoyed.
        const delay = Phaser.Math.Between(3000, 5000);
        this.time.delayedCall(delay, () => {
            this.indicator.complete().then(() => {
                if (this.scene.isActive()) this.scene.start('MainMenu');
            });
        });
    }
}
