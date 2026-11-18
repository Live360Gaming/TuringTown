import {Scene} from "phaser";
import {HOST_CENTER_X, HOST_CENTER_Y, HOST_HEIGHT, HOST_WIDTH} from "../../constants.js";
import {NetworkManager} from "../../core/network.js";
import {enterFixedSize} from "../../core/fitImage.js";
import {applyVolume, getVolume, setVolume} from "../../core/hostSettings.js";

const BTN = 0x3355aa;
const BTN_HOVER = 0x4466cc;
const BTN_DISABLED = 0x2a2f4d;
const PANEL = 0x222244;

const VOLUME_STEP = 5;
const SLIDER_WIDTH = 300;

export class Host extends Scene {
    constructor() {
        super("Host");
    }

    async create() {
        // Scale the game to fit the host screen 16:9
        enterFixedSize(this.scale, HOST_WIDTH, HOST_HEIGHT);

        // Server-authoritative room state; defaults are replaced once the room exists.
        this.state = {phase: 'Lobby', playerCount: 0, settings: {maxPlayers: 0, familyFriendly: true}, serverMaxPlayers: 0, minPlayers: 1};
        this.volume = getVolume();
        applyVolume(this.game, this.volume);

        this.events.once('shutdown', () => this.cleanup());

        // Lobby music loops until the scene shuts down.
        this.lobbyMusic = this.sound.add('lobby_music', {loop: true});
        this.lobbyMusic.play();

        // Add a background image for the host
        this.add.image(HOST_CENTER_X, HOST_CENTER_Y, 'background_tv');

        // Buttons first so they show even while the room is being created.
        this.createButton(HOST_CENTER_X, HOST_CENTER_Y + 120, 'Start Game', () => this.startGame());
        this.createButton(HOST_WIDTH - 100, 50, 'Settings', () => this.openSettings(), {width: 160, height: 50, fontSize: '24px'});

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
        this.closeSettings();
        this.lobbyMusic?.destroy();
        this.network?.destroy();
    }

    applyState(state) {
        this.state = state;
        this.refreshSettings();
    }

    startGame() {
        // No game scene exists yet; other code can listen for this event to begin the game.
        this.events.emit('startGame', {roomCode: this.roomCode});
    }

    openSettings() {
        if (this.settingsPanel) return;

        const cx = HOST_CENTER_X;
        const left = cx - 340;
        const panel = this.add.container(0, 0).setDepth(100);
        this.settingsPanel = panel;
        const ui = this.settingsUi = {};

        // Full-screen dim that also blocks clicks on the buttons underneath.
        const dim = this.add.rectangle(cx, HOST_CENTER_Y, HOST_WIDTH, HOST_HEIGHT, 0x000000, 0.6).setInteractive();
        const box = this.add.rectangle(cx, HOST_CENTER_Y, 760, 560, PANEL).setStrokeStyle(3, 0xffffff);
        const title = this.add.text(cx, 130, 'Settings', {fontSize: '40px', color: '#ffffff'}).setOrigin(0.5);

        const label = (y, text) => this.add.text(left, y, text, {fontSize: '30px', color: '#ffffff'}).setOrigin(0, 0.5);

        // Max Players: [-] n [+]
        ui.minus = this.createButton(cx + 130, 215, '-', () => this.changeMaxPlayers(-1), {width: 64, height: 64, fontSize: '36px'});
        ui.maxText = this.add.text(cx + 220, 215, '', {fontSize: '36px', color: '#ffffff'}).setOrigin(0.5);
        ui.plus = this.createButton(cx + 310, 215, '+', () => this.changeMaxPlayers(1), {width: 64, height: 64, fontSize: '36px'});

        // Volume slider
        const trackLeft = cx - 60;
        const trackY = 305;
        ui.track = this.add.rectangle(trackLeft + SLIDER_WIDTH / 2, trackY, SLIDER_WIDTH, 10, BTN).setStrokeStyle(2, 0xffffff)
            .setInteractive({useHandCursor: true});
        ui.handle = this.add.circle(trackLeft, trackY, 18, 0xffffff).setStrokeStyle(3, BTN)
            .setInteractive({useHandCursor: true, draggable: true});
        ui.volText = this.add.text(cx + 310, trackY, '', {fontSize: '30px', color: '#ffffff'}).setOrigin(0.5);
        const volFromX = x => Math.round(Math.min(1, Math.max(0, (x - trackLeft) / SLIDER_WIDTH)) * 100);
        ui.trackLeft = trackLeft;
        ui.track.on('pointerdown', p => this.setMasterVolume(volFromX(p.x)));
        ui.handle.on('drag', (_p, dragX) => this.setMasterVolume(volFromX(dragX)));

        // Family friendly toggle
        ui.family = this.createButton(cx + 220, 395, '', () => this.toggleFamilyFriendly(), {width: 180, height: 60, fontSize: '30px'});

        const fullscreenBtn = this.createButton(cx, 480, 'Toggle Fullscreen', () => {
            this.scale.isFullscreen ? this.scale.stopFullscreen() : this.scale.startFullscreen();
        }, {width: 360, height: 60, fontSize: '26px'});
        const closeBtn = this.createButton(cx, 560, 'Close', () => this.closeSettings(), {width: 200, height: 60, fontSize: '26px'});

        ui.message = this.add.text(cx, 610, '', {fontSize: '20px', color: '#ffcc66'}).setOrigin(0.5);

        panel.add([dim, box, title,
            label(215, 'Max Players'), ui.minus, ui.maxText, ui.plus,
            label(305, 'Volume'), ui.track, ui.handle, ui.volText,
            label(395, 'Family Friendly'), ui.family,
            fullscreenBtn, closeBtn, ui.message]);

        // Keyboard handlers exist only while the panel is open, so they can't stack up or touch gameplay input.
        this.input.keyboard.on('keydown-ESC', this.closeSettings, this);
        this.input.keyboard.on('keydown-LEFT', this.onVolumeKey, this);
        this.input.keyboard.on('keydown-RIGHT', this.onVolumeKey, this);
        this.input.keyboard.on('keydown-HOME', this.onVolumeKey, this);
        this.input.keyboard.on('keydown-END', this.onVolumeKey, this);

        this.refreshSettings();
    }

    closeSettings() {
        if (!this.settingsPanel) return;
        this.input.keyboard.off('keydown-ESC', this.closeSettings, this);
        this.input.keyboard.off('keydown-LEFT', this.onVolumeKey, this);
        this.input.keyboard.off('keydown-RIGHT', this.onVolumeKey, this);
        this.input.keyboard.off('keydown-HOME', this.onVolumeKey, this);
        this.input.keyboard.off('keydown-END', this.onVolumeKey, this);
        this.settingsPanel.destroy();
        this.settingsPanel = null;
        this.settingsUi = null;
    }

    /** Re-renders the settings controls from the current state. No-op while the panel is closed. */
    refreshSettings() {
        const ui = this.settingsUi;
        if (!ui) return;
        const {phase, playerCount, settings, serverMaxPlayers, minPlayers} = this.state;
        const locked = phase !== 'Lobby';
        const floor = Math.max(minPlayers, playerCount);

        ui.maxText.setText(String(settings.maxPlayers));
        ui.minus.setEnabled(!locked && settings.maxPlayers > floor);
        ui.plus.setEnabled(!locked && settings.maxPlayers < serverMaxPlayers);

        ui.family.setLabel(settings.familyFriendly ? 'ON' : 'OFF');
        ui.family.setEnabled(!locked);

        this.renderVolume();
        if (locked) this.showMessage('Locked during game');
    }

    renderVolume() {
        const ui = this.settingsUi;
        if (!ui) return;
        ui.handle.x = ui.trackLeft + (this.volume / 100) * SLIDER_WIDTH;
        ui.volText.setText(`${this.volume}%`);
    }

    setMasterVolume(value) {
        this.volume = setVolume(value);
        applyVolume(this.game, this.volume);
        this.renderVolume();
    }

    onVolumeKey(event) {
        const key = event.key;
        if (key === 'ArrowLeft') this.setMasterVolume(this.volume - VOLUME_STEP);
        else if (key === 'ArrowRight') this.setMasterVolume(this.volume + VOLUME_STEP);
        else if (key === 'Home') this.setMasterVolume(0);
        else if (key === 'End') this.setMasterVolume(100);
    }

    changeMaxPlayers(delta) {
        this.updateSettings({maxPlayers: this.state.settings.maxPlayers + delta});
    }

    toggleFamilyFriendly() {
        this.updateSettings({familyFriendly: !this.state.settings.familyFriendly});
    }

    /** The server is authoritative: render whatever it returns, show its error otherwise. */
    async updateSettings(change) {
        try {
            this.applyState(await this.network.put(`/api/room/${encodeURIComponent(this.roomCode)}/settings`, change));
            this.showMessage('');
        } catch (err) {
            this.showMessage(err.message);
        }
    }

    showMessage(text) {
        this.settingsUi?.message.setText(text);
    }

    /** Simple rectangle + label button. Returns a container (with setEnabled/setLabel) so it can be grouped into panels. */
    createButton(x, y, label, onClick, {width = 300, height = 70, fontSize = '32px'} = {}) {
        let enabled = true;
        const bg = this.add.rectangle(0, 0, width, height, BTN)
            .setStrokeStyle(2, 0xffffff)
            .setInteractive({useHandCursor: true})
            .on('pointerover', () => enabled && bg.setFillStyle(BTN_HOVER))
            .on('pointerout', () => bg.setFillStyle(enabled ? BTN : BTN_DISABLED))
            .on('pointerup', () => enabled && onClick());
        const text = this.add.text(0, 0, label, {fontSize, color: '#ffffff'}).setOrigin(0.5);
        const button = this.add.container(x, y, [bg, text]);
        button.setEnabled = on => {
            enabled = on;
            bg.setFillStyle(on ? BTN : BTN_DISABLED);
            bg.input.cursor = on ? 'pointer' : 'default';
            text.setAlpha(on ? 1 : 0.4);
        };
        button.setLabel = value => text.setText(value);
        return button;
    }
}
