import {Scene} from "phaser";
import {TV_CENTER_X, TV_CENTER_Y, TV_HEIGHT, TV_WIDTH} from "../constants.js";

export class Tv extends Scene {
    constructor() {
        super("Tv");
    }

    create() {
        // Scale the game to fit the TV 16:9
        this.scale.setGameSize(TV_WIDTH, TV_HEIGHT);

        // Add a background image for the TV
        this.add.image(TV_CENTER_X, TV_CENTER_Y, 'background_tv');
    }
}