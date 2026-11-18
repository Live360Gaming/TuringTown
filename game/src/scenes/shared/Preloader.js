import { Scene } from 'phaser';

// Loading screen to download all the assets before starting the game.
export class Preloader extends Scene
{
    constructor ()
    {
        super('Preloader');
    }

    init ()
    {
        let mainCamera = this.cameras.main;
        
        //  We loaded this image in our Boot Scene, so we can display it here
        // TODO: Make this fit the screen
        this.add.image(mainCamera.centerX, mainCamera.centerY, 'preloader');
    }

    preload ()
    {
        //  Load the assets for the game - Replace with the path to your own assets
        this.load.setPath('assets');

        this.load.image('join_phone_btn', 'join_phone_btn.jpg');
        this.load.image('start_tv_btn', 'start_tv_btn.jpg');
        this.load.image('background', 'background.jpg');
        this.load.image('background_tv', 'background_tv.jpg');
    }

    create ()
    {
        // Now switch to the main menu.
        this.scene.start('MainMenu');
    }
}
