import { Scene } from 'phaser';
import { fitImageToScreen } from '../../core/scaling.js';

// This scene is a preloader for the preloader, think of it like a loading screen for the initial loading screen.
// Similar to how a BIOS actually boots the OS which needs to boot itself.
export class Boot extends Scene
{
    constructor ()
    {
        super('Boot');
    }

    preload ()
    {
        // Preload the loading screen image.
        // TODO: Placeholder AI slop preloader image.
        this.load.image('preloader', 'assets/preloader.png');
    }

    create ()
    {
        fitImageToScreen(this, 'preloader');
        document.getElementById('boot-splash')?.remove();


        // Now switch to the loading screen.
        this.scene.start('Preloader');
    }
}
