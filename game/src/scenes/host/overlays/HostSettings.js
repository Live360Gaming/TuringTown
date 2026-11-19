import {Scene} from "phaser";
import {HOST_CENTER_X, HOST_CENTER_Y, HOST_HEIGHT, HOST_WIDTH} from "../../../constants.js";
import {applyVolume, getVolume, setVolume} from "../../../core/hostSettings.js";
import {BTN, createTextButton, PANEL} from "../../../core/ui/Button.js";

const VOLUME_STEP = 5;
const SLIDER_WIDTH = 300;

/**
 * Host settings panel, launched on top of the Host lobby.
 * Room settings are server-authoritative: this scene renders the Host's latest state (pushed via the Host's
 * 'state' event) and hands server responses back to the Host. Volume is local to this machine.
 */
export class HostSettings extends Scene {
    constructor() {
        super("HostSettings");
    }

    create() {
        this.host = this.scene.get('Host');
        this.volume = getVolume();

        const cx = HOST_CENTER_X;
        const left = cx - 340;
        const ui = this.ui = {};

        // Full-screen dim that also blocks clicks on the lobby underneath.
        this.add.rectangle(cx, HOST_CENTER_Y, HOST_WIDTH, HOST_HEIGHT, 0x000000, 0.6).setInteractive();
        this.add.rectangle(cx, HOST_CENTER_Y, 760, 560, PANEL).setStrokeStyle(3, 0xffffff);
        this.add.text(cx, 130, 'Settings', {fontSize: '40px', color: '#ffffff'}).setOrigin(0.5);

        const label = (y, text) => this.add.text(left, y, text, {fontSize: '30px', color: '#ffffff'}).setOrigin(0, 0.5);

        // Max Players: [-] n [+]
        label(215, 'Max Players');
        ui.minus = createTextButton(this, cx + 130, 215, '-', () => this.changeMaxPlayers(-1), {width: 64, height: 64, fontSize: '36px'});
        ui.maxText = this.add.text(cx + 220, 215, '', {fontSize: '36px', color: '#ffffff'}).setOrigin(0.5);
        ui.plus = createTextButton(this, cx + 310, 215, '+', () => this.changeMaxPlayers(1), {width: 64, height: 64, fontSize: '36px'});

        // Volume slider
        label(305, 'Volume');
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
        label(395, 'Family Friendly');
        ui.family = createTextButton(this, cx + 220, 395, '', () => this.toggleFamilyFriendly(), {width: 180, height: 60, fontSize: '30px'});

        createTextButton(this, cx, 480, 'Toggle Fullscreen', () => {
            this.scale.isFullscreen ? this.scale.stopFullscreen() : this.scale.startFullscreen();
        }, {width: 360, height: 60, fontSize: '26px'});
        createTextButton(this, cx - 120, 560, 'Close', () => this.scene.stop(), {width: 200, height: 60, fontSize: '26px'});
        createTextButton(this, cx + 120, 560, 'Quit', () => this.host.quit(), {width: 200, height: 60, fontSize: '26px'});

        ui.message = this.add.text(cx, 610, '', {fontSize: '20px', color: '#ffcc66'}).setOrigin(0.5);

        // Listeners on this scene's keyboard are removed automatically when the scene stops.
        this.input.keyboard.on('keydown-ESC', () => this.scene.stop());
        this.input.keyboard.on('keydown-LEFT', this.onVolumeKey, this);
        this.input.keyboard.on('keydown-RIGHT', this.onVolumeKey, this);
        this.input.keyboard.on('keydown-HOME', this.onVolumeKey, this);
        this.input.keyboard.on('keydown-END', this.onVolumeKey, this);

        // The Host's events outlive this scene, so this listener must be removed by hand.
        this.host.events.on('state', this.refresh, this);
        this.events.once('shutdown', () => this.host.events.off('state', this.refresh, this));

        this.refresh(this.host.state);
    }

    /** Re-renders the settings controls from the given room state. */
    refresh(state) {
        const ui = this.ui;
        const {phase, playerCount, settings, serverMaxPlayers, minPlayers} = state;
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
        const ui = this.ui;
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
        this.updateSettings({maxPlayers: this.host.state.settings.maxPlayers + delta});
    }

    toggleFamilyFriendly() {
        this.updateSettings({familyFriendly: !this.host.state.settings.familyFriendly});
    }

    /** The server is authoritative: render whatever it returns, show its error otherwise. */
    async updateSettings(change) {
        const {network, roomCode} = this.host;
        try {
            // Applying on the Host emits 'state', which re-renders this panel.
            this.host.applyState(await network.put(`/api/room/${encodeURIComponent(roomCode)}/settings`, change));
            this.showMessage('');
        } catch (err) {
            this.showMessage(err.message);
        }
    }

    showMessage(text) {
        // The panel may have been closed while the request was in flight.
        if (this.sys.isActive()) this.ui.message.setText(text);
    }
}
