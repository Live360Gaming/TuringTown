import * as Phaser from 'phaser';
import { Scene } from 'phaser';
import { fitImageToScreen } from '../../core/scaling.js';
import { LoadingIndicator } from '../../core/ui/LoadingIndicator.js';
import { CHARACTER_KEYS, PLACEHOLDER_KEYS, PLACEHOLDER_ROBOT_KEY, ROBOT_KEYS } from '../../constants.js';
import { loadFonts } from '../../core/theme.js';
import { assetPath } from '../../core/season.js';

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

        // Start fetching the fonts now; they are waited for (at most 3s in total) before the menu opens.
        this.fontsReady = loadFonts(3000);

        this.load.setPath('assets');

        this.load.image('join_phone_btn', assetPath('join_phone_btn', 'join_phone_btn.jpg'));
        this.load.image('start_tv_btn', assetPath('start_tv_btn', 'start_tv_btn.jpg'));
        this.load.image('background', assetPath('background', 'background.jpg'));
        this.load.image('background_tv', assetPath('background_tv', 'background_tv.jpg'));
        this.load.image('table', 'table.png');

        // The characters, and the robots that are only shown once a bot has been found (see core/session.js).
        this.load.setPath('assets/characters');
        [...CHARACTER_KEYS, ...ROBOT_KEYS, ...PLACEHOLDER_KEYS, PLACEHOLDER_ROBOT_KEY].forEach(key => this.load.image(key, `${key}.png`));

        this.load.setPath('audio');
        this.load.audio('lobby_music', 'lobby_music.mp3');
        // Chat sounds shared by every game: message sent, message received, and a message that mentions you.
        this.load.audio('message_sfx1', 'shared/sfx/message_sfx1.mp3');
        this.load.audio('message_sfx2', 'shared/sfx/message_sfx2.mp3');
        this.load.audio('message_sfx3', 'shared/sfx/message_sfx3.mp3');
    }

    create ()
    {
        // The loader has finished by the time create() runs; failed files are reported here.
        if (this.failed.length > 0) {
            this.indicator.fail(`Failed to load: ${this.failed.join(', ')}`, () => this.scene.restart());
            return;
        }

        // Show the green confirmed status, then hold it for a random 3-5s before moving on.
        this.indicator.complete().then(() => {
            const delay = Phaser.Math.Between(3000, 5000);
            this.time.delayedCall(delay, () => {
                this.fontsReady.then(() => {
                    if (this.scene.isActive()) this.scene.start('MainMenu');
                });
            });
        });
    }
}
