import {Scene} from "phaser";
import {HOST_CENTER_X, HOST_CENTER_Y, HOST_HEIGHT, HOST_WIDTH} from "../../constants.js";
import {NetworkManager} from "../../core/network.js";
import {enterFixedSize, fitImageToScreen} from "../../core/scaling.js";
import {applyVolume} from "../../core/hostSettings.js";
import {createTextButton} from "../../core/ui/Button.js";
import {hubErrorMessage} from "../../core/gameHub.js";
import {createAvatar} from "../../core/characters.js";
import {GameSession, getSession, leaveToMenu, setSession} from "../../core/session.js";

// Overlay scenes launched on top of the lobby (see scenes/host/overlays).
const OVERLAYS = ['HostSettings', 'QuitConfirm'];

// The host's lobby. It creates the room once, then keeps the GameSession for the whole visit: when a game ends the
// director brings the host back here and the same room is reused (see core/director.js).
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

        this.closed = false;
        this.starting = false;
        this.rosterItems = [];
        this.events.once('shutdown', () => this.cleanup());

        // Lobby music loops until the scene shuts down.
        this.lobbyMusic = this.sound.add('lobby_music', {loop: true});
        this.lobbyMusic.play();

        // Add a background image for the host
        fitImageToScreen(this, 'background_tv');

        // ESC asks to quit. While an overlay is open this scene's input is off, so the overlay handles ESC instead.
        this.input.keyboard.on('keydown-ESC', () => this.openOverlay('QuitConfirm'));

        // Buttons first so they show even while the room is being created.
        // Disabled until enough players have joined (see applyState).
        this.startBtn = createTextButton(this, HOST_CENTER_X, HOST_CENTER_Y + 120, 'Start Game', () => this.startGame());
        this.startBtn.setEnabled(false);
        this.statusText = this.add.text(HOST_CENTER_X, HOST_CENTER_Y + 200, '', {fontSize: '24px', color: '#ffffff'}).setOrigin(0.5);
        createTextButton(this, HOST_WIDTH - 100, 50, 'Settings', () => this.openOverlay('HostSettings'), {width: 160, height: 50, fontSize: '24px'});

        // Initialize the network manager
        this.network = new NetworkManager({baseUrl: window.location.origin});

        try {
            await this.openRoom();
        } catch (err) {
            if (!this.closed) this.statusText.setText(err.message);
        }
    }

    /** Creates the room (or reuses the one from before the game) and connects to it. */
    async openRoom() {
        let session = getSession(this);
        if (session?.role !== 'host') {
            const room = await this.network.post('/api/room/create');
            if (this.closed) return;

            session = new GameSession(this.game, {role: 'host', roomCode: room.roomCode, hostToken: room.hostToken, lobbyKey: 'Host'});
            setSession(this.game, session);
            this.applyState(room.state);
        } else {
            this.applyState(await this.network.get(`/api/room/${encodeURIComponent(session.roomCode)}`));
            if (this.closed) return;
        }

        this.session = session;
        this.roomCode = session.roomCode;
        this.network.setHeader('X-Host-Token', session.hostToken);

        this.add.text(HOST_CENTER_X, HOST_CENTER_Y, `Room Code: ${this.roomCode}`, {fontSize: '32px', color: '#ffffff'}).setOrigin(0.5);

        // Keep the player count / settings fresh (limits depend on who has joined).
        this.network.on('poll:room', state => this.applyState(state));
        this.network.startPolling('room', `/api/room/${encodeURIComponent(this.roomCode)}`, 1000);

        // Who has joined, shown as their avatars and aliases.
        session.on('roster', this.drawRoster, this);

        // Real-time connection: lets the host start the game and moves every screen along with it.
        if (!session.connection) {
            await session.connect();
            if (this.closed) return;
            session.attachDirector();
        }
        this.drawRoster(session.roster);
    }

    cleanup() {
        OVERLAYS.forEach(key => this.scene.stop(key));
        this.lobbyMusic?.destroy();
        this.network?.destroy();
        this.session?.off('roster', this.drawRoster, this);
        this.closed = true;
        // The session stays open: it carries the room through the game. Quit closes it (see quit()).
    }

    /** Draws a row of the players who have joined. */
    drawRoster(roster) {
        this.rosterItems.forEach(item => item.destroy());
        this.rosterItems = [];

        const gap = Math.min(170, (HOST_WIDTH - 120) / Math.max(roster.length, 1));
        const startX = HOST_CENTER_X - ((roster.length - 1) * gap) / 2;
        roster.forEach((player, i) => {
            const x = startX + i * gap;
            this.rosterItems.push(createAvatar(this, x, 200, 84, player, {dim: !player.connected}));
            this.rosterItems.push(this.add.text(x, 262, player.alias, {fontSize: '20px', color: player.color}).setOrigin(0.5));
        });
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

    /** Leave the lobby for good: closes the room connection and goes back to the main menu. */
    quit() {
        leaveToMenu(this);
    }

    /** Stores the latest server state and tells any open overlay (e.g. HostSettings) about it. */
    applyState(state) {
        this.state = state;
        this.startBtn?.setEnabled(state.phase === 'Lobby' && state.playerCount >= state.minPlayers);
        this.events.emit('state', state);
    }

    async startGame() {
        if (!this.session?.connection || this.starting) return;
        this.starting = true;
        this.statusText.setText('');
        try {
            // The server moves everyone to the tutorial; the director then switches screens (see core/director.js).
            await this.session.invoke('StartGame');
        } catch (err) {
            this.statusText.setText(hubErrorMessage(err));
        } finally {
            this.starting = false;
        }
    }
}
