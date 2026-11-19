import {Scene} from "phaser";
import {HOST_CENTER_X, HOST_CENTER_Y, HOST_HEIGHT, HOST_WIDTH} from "../../../constants.js";
import {createTextButton, PANEL} from "../../../core/ui/Button.js";

/** "Would you like to quit?" dialog, launched on top of the Host lobby. ESC or Cancel closes it. */
export class QuitConfirm extends Scene {
    constructor() {
        super("QuitConfirm");
    }

    create() {
        const cx = HOST_CENTER_X;
        const cy = HOST_CENTER_Y;
        this.add.rectangle(cx, cy, HOST_WIDTH, HOST_HEIGHT, 0x000000, 0.6).setInteractive();
        this.add.rectangle(cx, cy, 560, 260, PANEL).setStrokeStyle(3, 0xffffff);
        this.add.text(cx, cy - 55, 'Would you like to quit?', {fontSize: '36px', color: '#ffffff'}).setOrigin(0.5);
        createTextButton(this, cx - 130, cy + 55, 'Cancel', () => this.scene.stop(), {width: 200, height: 60, fontSize: '26px'});
        createTextButton(this, cx + 130, cy + 55, 'Quit', () => this.scene.get('Host').quit(), {width: 200, height: 60, fontSize: '26px'});

        // Listeners on this scene's keyboard are removed automatically when the scene stops.
        this.input.keyboard.on('keydown-ESC', () => this.scene.stop());
    }
}
