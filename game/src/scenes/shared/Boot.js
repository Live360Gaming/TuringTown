import { Scene } from 'phaser';

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
        this.load.image('preloader', 'assets/preloader.jpg');
    }

    create ()
    {
        let mainCamera = this.cameras.main;

        //  We loaded this image in our Boot Scene, so we can display it here
        // TODO: Make this fit the screen
        this.add.image(mainCamera.centerX, mainCamera.centerY, 'preloader');
        
        // Now switch to the loading screen.
        this.scene.start('Preloader');
    }
}
