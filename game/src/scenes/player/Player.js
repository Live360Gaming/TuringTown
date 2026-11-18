import * as Phaser from "phaser";
import {Scene} from "phaser";
import {NetworkManager} from "../../core/network.js";

// Players can be on a phone or a laptop, so unlike the Host this scene fills the whole window
// (Phaser RESIZE mode) and lays itself out from the real screen size instead of a fixed resolution.
export class Player extends Scene {
    constructor() {
        super("Player");
    }

    create() {
        this.enterFullWindow();

        // Initialize the network manager
        this.network = new NetworkManager({baseUrl: window.location.origin});

        this.background = this.add.image(0, 0, 'background');

        this.titleText = this.add.text(0, 0, 'Enter Room Code', {color: '#ffffff'})
            .setOrigin(0.5)
            .setResolution(window.devicePixelRatio);

        // A real HTML input so phones bring up their keyboard.
        this.codeInput = this.add.dom(0, 0, 'input', {
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

        this.joinBtn = this.add.image(0, 0, 'join_phone_btn')
            .setInteractive({useHandCursor: true})
            .on('pointerup', () => this.join());

        this.layout();
        this.scale.on('resize', this.layout, this);
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

        this.titleText.setPosition(cx, cy - 200 * s).setFontSize(48 * s);
        this.statusText.setPosition(cx, cy + 40 * s).setFontSize(32 * s);

        if (this.codeInput.active) {
            this.codeInput.setPosition(cx, cy - 80 * s);
            this.codeInput.node.style.width = `${480 * s}px`;
            this.codeInput.node.style.fontSize = `${48 * s}px`;
        }

        if (this.joinBtn.active) {
            this.joinBtn.setPosition(cx, cy + 200 * s).setScale(s);
        }
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
        } catch (err) {
            this.statusText.setText(err.message);
        } finally {
            this.joining = false;
        }
    }

    // Switch the (game-wide) scale manager to fill the window while this scene runs,
    // and put it back to FIT for the fixed-resolution scenes when it stops.
    enterFullWindow() {
        const scale = this.scale;

        scale.scaleMode = Phaser.Scale.RESIZE;
        // FIT locks the display to the old aspect ratio and leaves a CSS size on the canvas; clear both.
        scale.displaySize.setAspectMode(Phaser.Structs.Size.NONE);
        scale.canvas.style.width = '';
        scale.canvas.style.height = '';
        scale.refresh();

        this.events.once('shutdown', () => {
            scale.off('resize', this.layout, this);
            scale.scaleMode = Phaser.Scale.FIT;
            scale.displaySize.setAspectMode(Phaser.Structs.Size.FIT);
            scale.refresh();
        });
    }
}
