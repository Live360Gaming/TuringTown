import {Scene} from "phaser";
import {HOST_CENTER_X, HOST_CENTER_Y, HOST_HEIGHT, HOST_WIDTH} from "../../constants.js";
import {NetworkManager} from "../../core/network.js";

export class Host extends Scene {
    constructor() {
        super("Host");
    }

    async create() {
        // Scale the game to fit the host screen 16:9
        this.scale.setGameSize(HOST_WIDTH, HOST_HEIGHT);

        // Add a background image for the host
        this.add.image(HOST_CENTER_X, HOST_CENTER_Y, 'background_tv');

        // Buttons first so they show even while the room is being created.
        this.createButton(HOST_CENTER_X, HOST_CENTER_Y + 120, 'Start Game', () => this.startGame());
        this.createButton(HOST_WIDTH - 100, 50, 'Settings', () => this.openSettings(), {width: 160, height: 50, fontSize: '24px'});

        // Initialize the network manager
        this.network = new NetworkManager({baseUrl: window.location.origin});

        this.roomCode = (await this.network.post('/api/room/create')).roomCode;

        this.add.text(HOST_CENTER_X, HOST_CENTER_Y, `Room Code: ${this.roomCode}`, {fontSize: '32px', color: '#ffffff'}).setOrigin(0.5);
    }

    startGame() {
        // No game scene exists yet; other code can listen for this event to begin the game.
        this.events.emit('startGame', {roomCode: this.roomCode});
    }

    openSettings() {
        if (this.settingsPanel) return;

        const panel = this.add.container(0, 0).setDepth(100);
        this.settingsPanel = panel;

        // Full-screen dim that also blocks clicks on the buttons underneath.
        const dim = this.add.rectangle(HOST_CENTER_X, HOST_CENTER_Y, HOST_WIDTH, HOST_HEIGHT, 0x000000, 0.6)
            .setInteractive();
        const box = this.add.rectangle(HOST_CENTER_X, HOST_CENTER_Y, 600, 400, 0x222244).setStrokeStyle(3, 0xffffff);
        const title = this.add.text(HOST_CENTER_X, HOST_CENTER_Y - 150, 'Settings', {fontSize: '40px', color: '#ffffff'}).setOrigin(0.5);

        const fullscreenBtn = this.createButton(HOST_CENTER_X, HOST_CENTER_Y - 30, 'Toggle Fullscreen', () => {
            this.scale.isFullscreen ? this.scale.stopFullscreen() : this.scale.startFullscreen();
        }, {width: 360, height: 60, fontSize: '26px'});
        const closeBtn = this.createButton(HOST_CENTER_X, HOST_CENTER_Y + 120, 'Close', () => this.closeSettings(),
            {width: 200, height: 60, fontSize: '26px'});

        panel.add([dim, box, title, fullscreenBtn, closeBtn]);
    }

    closeSettings() {
        this.settingsPanel?.destroy();
        this.settingsPanel = null;
    }

    /** Simple rectangle + label button. Returns a container so it can be grouped into panels. */
    createButton(x, y, label, onClick, {width = 300, height = 70, fontSize = '32px'} = {}) {
        const bg = this.add.rectangle(0, 0, width, height, 0x3355aa)
            .setStrokeStyle(2, 0xffffff)
            .setInteractive({useHandCursor: true})
            .on('pointerover', () => bg.setFillStyle(0x4466cc))
            .on('pointerout', () => bg.setFillStyle(0x3355aa))
            .on('pointerup', onClick);
        const text = this.add.text(0, 0, label, {fontSize, color: '#ffffff'}).setOrigin(0.5);
        return this.add.container(x, y, [bg, text]);
    }
}
