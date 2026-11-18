import {Scene} from "phaser";
import {TV_CENTER_X, TV_CENTER_Y, TV_HEIGHT, TV_WIDTH} from "../../constants.js";
import {NetworkManager} from "../../core/network.js";

export class Tv extends Scene {
    constructor() {
        super("Tv");
    }

    async create() {
        // Scale the game to fit the TV 16:9
        this.scale.setGameSize(TV_WIDTH, TV_HEIGHT);

        // Add a background image for the TV
        this.add.image(TV_CENTER_X, TV_CENTER_Y, 'background_tv');

        // Initialize the network manager
        this.network = new NetworkManager({baseUrl: window.location.origin});

        var roomCode = (await this.network.post('/api/room/create')).roomCode;

        this.add.text(TV_CENTER_X, TV_CENTER_Y, `Room Code: ${roomCode}`, { fontSize: '32px', color: '#ffffff' }).setOrigin(0.5);
    }
}