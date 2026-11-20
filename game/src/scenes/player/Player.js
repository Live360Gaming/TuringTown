import * as Phaser from "phaser";
import {Scene} from "phaser";
import {NetworkManager} from "../../core/network.js";
import {enterFullWindow} from "../../core/scaling.js";
import {createTextButton, PANEL} from "../../core/ui/Button.js";

// Players can be on a phone or a laptop, so unlike the Host this scene fills the whole window
// (Phaser RESIZE mode) and lays itself out from the real screen size instead of a fixed resolution.
export class Player extends Scene {
    constructor() {
        super("Player");
    }

    create() {
        enterFullWindow(this.scale);

        // Initialize the network manager
        this.network = new NetworkManager({baseUrl: window.location.origin});

        this.background = this.add.image(0, 0, 'background');

        // Rounded card the form sits on (redrawn in layout()).
        this.panel = this.add.graphics();

        this.titleText = this.add.text(0, 0, 'Enter Room Code', {color: '#ffffff'})
            .setOrigin(0.5)
            .setResolution(window.devicePixelRatio);

        // A real HTML input so phones bring up their keyboard.
        this.codeInput = this.add.dom(0, 0, 'input', {
            boxSizing: 'border-box',
            padding: '0.3em',
            textAlign: 'center',
            textTransform: 'uppercase',
            borderRadius: '12px',
            border: 'none',
        });
        const input = this.codeInput.node;
        input.placeholder = 'AABB-CCDD';
        input.maxLength = 9;
        input.autocomplete = 'off';
        input.autocapitalize = 'characters';
        input.spellcheck = false;
        // Auto-format as the player types: letters/digits only, uppercase, dash after the 4th character.
        input.addEventListener('input', () => {
            const raw = input.value.replace(/[^a-z0-9]/gi, '').toUpperCase().slice(0, 8);
            input.value = raw.length > 4 ? `${raw.slice(0, 4)}-${raw.slice(4)}` : raw;
        });
        input.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') this.join();
        });

        this.statusText = this.add.text(0, 0, '', {color: '#ffffff', align: 'center'})
            .setOrigin(0.5)
            .setResolution(window.devicePixelRatio);

        this.joinBtn = createTextButton(this, 0, 0, 'Join', () => this.join(), {width: 360, height: 80, fontSize: '40px'});
        this.quitBtn = createTextButton(this, 0, 0, 'Quit', () => this.quit(), {width: 360, height: 80, fontSize: '40px'});

        this.input.keyboard.on('keydown-ESC', () => this.quit());

        this.layout();
        this.scale.on('resize', this.layout, this);
        this.events.once('shutdown', () => this.scale.off('resize', this.layout, this));
    }

    /** Position and size everything from the current window size. */
    layout() {
        const {width, height} = this.scale;
        const cx = width / 2;
        const cy = height / 2;

        // UI scale: 1 at 720px on the short side, clamped so it stays readable on tiny/huge screens.
        const s = Phaser.Math.Clamp(Math.min(width, height) / 720, 0.5, 1.5);

        // Background covers the whole window (cropping rather than letterboxing).
        this.background.setPosition(cx, cy)
            .setScale(Math.max(width / this.background.width, height / this.background.height));

        // Card: portrait panel centred on screen, like the mockup.
        const panelW = 560 * s;
        const panelH = 700 * s;
        this.panel.clear()
            .fillStyle(PANEL, 1)
            .fillRoundedRect(cx - panelW / 2, cy - panelH / 2, panelW, panelH, 28 * s)
            .lineStyle(3, 0xffffff, 1)
            .strokeRoundedRect(cx - panelW / 2, cy - panelH / 2, panelW, panelH, 28 * s);

        this.titleText.setPosition(cx, cy - 250 * s).setFontSize(44 * s);
        this.statusText.setPosition(cx, cy - 30 * s).setFontSize(28 * s)
            .setWordWrapWidth(panelW - 60 * s);

        if (this.codeInput.active) {
            this.codeInput.setPosition(cx, cy - 150 * s);
            this.codeInput.node.style.width = `${420 * s}px`;
            this.codeInput.node.style.fontSize = `${44 * s}px`;
            // Phaser caches the element's size for centring, so re-measure after changing it.
            this.codeInput.updateSize();
        }

        if (this.joinBtn.active) {
            this.joinBtn.setPosition(cx, cy + 100 * s).setScale(s);
        }
        // After joining there is no Join button, so Quit moves up into its place.
        this.quitBtn.setPosition(cx, cy + (this.joinBtn.active ? 220 : 100) * s).setScale(s);
    }

    /** Leave the join screen and go back to the main menu. */
    quit() {
        this.scene.start('MainMenu');
    }

    async join() {
        const roomCode = this.codeInput.node.value.trim().toUpperCase();
        if (!roomCode || this.joining) return;

        this.joining = true;
        this.statusText.setText('Joining...');

        try {
            const {playerId} = await this.network.post('/api/room/join', {roomCode});
            this.playerId = playerId;
            this.roomCode = roomCode;

            // Joined: swap the form out for a waiting message.
            this.codeInput.destroy();
            this.joinBtn.destroy();
            this.statusText.setText(`Joined ${roomCode}\nWaiting for host...`);
            this.layout();
        } catch (err) {
            this.statusText.setText(err.message);
        } finally {
            this.joining = false;
        }
    }

}
