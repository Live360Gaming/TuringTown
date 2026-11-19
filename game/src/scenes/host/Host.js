import {Scene} from "phaser";
import {HOST_CENTER_X, HOST_CENTER_Y, HOST_HEIGHT, HOST_WIDTH} from "../../constants.js";
import {NetworkManager} from "../../core/network.js";
import {enterFixedSize} from "../../core/scaling.js";
import {applyVolume} from "../../core/hostSettings.js";
import {createTextButton} from "../../core/ui/Button.js";

// Overlay scenes launched on top of the lobby (see scenes/host/overlays).
const OVERLAYS = ['HostSettings', 'QuitConfirm'];

export class Host extends Scene {
    constructor() {
        super("Host");
    }

    async create() {
        // Scale the game to fit the host screen 16:9
        enterFixedSize(this.scale, HOST_WIDTH, HOST_HEIGHT);

        // Server-authoritative room state; defaults are replaced once the room exists.
        this.state = {phase: 'Lobby', playerCount: 0, settings: {maxPlayers: 0, familyFriendly: true}, serverMaxPlayers: 0, minPlayers: 1};
        applyVolume(this.game);
        // Input may have been left off if the scene stopped while an overlay was open.
        this.setInputEnabled(true);

        this.events.once('shutdown', () => this.cleanup());

        // Lobby music loops until the scene shuts down.
        this.lobbyMusic = this.sound.add('lobby_music', {loop: true});
        this.lobbyMusic.play();

        // Add a background image for the host
        this.add.image(HOST_CENTER_X, HOST_CENTER_Y, 'background_tv');

        // ESC asks to quit. While an overlay is open this scene's input is off, so the overlay handles ESC instead.
        this.input.keyboard.on('keydown-ESC', () => this.openOverlay('QuitConfirm'));

        // Buttons first so they show even while the room is being created.
        createTextButton(this, HOST_CENTER_X, HOST_CENTER_Y + 120, 'Start Game', () => this.startGame());
        createTextButton(this, HOST_WIDTH - 100, 50, 'Settings', () => this.openOverlay('HostSettings'), {width: 160, height: 50, fontSize: '24px'});

        // Initialize the network manager
        this.network = new NetworkManager({baseUrl: window.location.origin});

        const room = await this.network.post('/api/room/create');
        this.roomCode = room.roomCode;
        this.network.setHeader('X-Host-Token', room.hostToken);
        this.applyState(room.state);

        this.add.text(HOST_CENTER_X, HOST_CENTER_Y, `Room Code: ${this.roomCode}`, {fontSize: '32px', color: '#ffffff'}).setOrigin(0.5);

        // Keep the player count / settings fresh (limits depend on who has joined).
        this.network.on('poll:room', state => this.applyState(state));
        this.network.startPolling('room', `/api/room/${encodeURIComponent(this.roomCode)}`, 1000);
    }

    cleanup() {
        OVERLAYS.forEach(key => this.scene.stop(key));
        this.lobbyMusic?.destroy();
        this.network?.destroy();
    }

    /** Runs an overlay scene on top of the lobby and blocks lobby input (pointer and keyboard) until it closes. */
    openOverlay(key) {
        this.setInputEnabled(false);
        this.scene.launch(key);
        this.scene.get(key).events.once('shutdown', () => this.setInputEnabled(true));
    }

    setInputEnabled(on) {
        // The keyboard plugin has its own flag; disabling the input plugin alone doesn't stop key events.
        this.input.enabled = on;
        this.input.keyboard.enabled = on;
    }

    /** Leave the lobby and go back to the main menu. */
    quit() {
        this.scene.start('MainMenu');
    }

    /** Stores the latest server state and tells any open overlay (e.g. HostSettings) about it. */
    applyState(state) {
        this.state = state;
        this.events.emit('state', state);
    }

    startGame() {
        // No game scene exists yet; other code can listen for this event to begin the game.
        this.events.emit('startGame', {roomCode: this.roomCode});
    }
}
