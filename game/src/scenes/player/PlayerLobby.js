import * as Phaser from "phaser";
import {Scene} from "phaser";
import {enterFullWindow} from "../../core/scaling.js";
import {createTextButton, PANEL} from "../../core/ui/Button.js";
import {hubErrorMessage} from "../../core/gameHub.js";
import {createAvatar} from "../../core/characters.js";
import {clearCredentials, GameSession, getSession, leaveToMenu, setSession} from "../../core/session.js";

// Waiting screen: the player has joined and sees the alias and character the server gave them until the host
// starts the game. It also keeps the GameSession for the whole visit; after a game ends the director brings the
// player back here and the same session is reused (see core/director.js).
export class PlayerLobby extends Scene {
    constructor() {
        super("PlayerLobby");
    }

    init(data) {
        // From the Player (join) scene: {roomCode, playerId, playerToken}. Empty when the director returns us here.
        this.joinData = data?.roomCode ? data : null;
    }

    create() {
        enterFullWindow(this.scale);
        this.closed = false;

        this.background = this.add.image(0, 0, 'background');
        this.panel = this.add.graphics();
        this.avatar = null;
        this.nameText = this.add.text(0, 0, '', {color: '#ffffff'}).setOrigin(0.5).setResolution(window.devicePixelRatio);
        this.titleText = this.add.text(0, 0, '', {color: '#ffffff'}).setOrigin(0.5).setResolution(window.devicePixelRatio);
        this.statusText = this.add.text(0, 0, 'Connecting...', {color: '#ffffff', align: 'center'}).setOrigin(0.5).setResolution(window.devicePixelRatio);
        this.quitBtn = createTextButton(this, 0, 0, 'Quit', () => this.quit(), {width: 360, height: 80, fontSize: '40px'});

        this.input.keyboard.on('keydown-ESC', () => this.quit());

        this.layout();
        this.scale.on('resize', this.layout, this);
        this.events.once('shutdown', () => this.cleanup());

        this.connect();
    }

    async connect() {
        let session = getSession(this);

        if (this.joinData) {
            const {roomCode, playerId, playerToken} = this.joinData;
            session = new GameSession(this.game, {role: 'player', roomCode, playerId, playerToken, lobbyKey: 'PlayerLobby'});
            setSession(this.game, session);
        } else if (session?.role !== 'player') {
            this.scene.start('MainMenu');
            return;
        }

        this.session = session;
        try {
            if (!session.connection) {
                await session.connect();
                // The scene may have been left while we were connecting.
                if (this.closed) return;
                session.attachDirector();
            }
            this.show();
            // A refresh, or a return after a game, may already be past the lobby; the director has moved us on if so.
            this.session.on('roster', this.show, this);
        } catch (err) {
            // The room is gone or the seat is no longer ours (e.g. the server restarted): forget it, so the
            // next visit to the join screen asks for a code again.
            clearCredentials();
            if (!this.closed) this.statusText.setText(hubErrorMessage(err));
        }
    }

    /** Shows who this player is and what is happening. */
    show() {
        const you = this.session.you;
        // The director may already have moved us on to a game screen.
        if (!you || this.closed || !this.scene.isActive()) return;

        this.avatar?.destroy();
        this.avatar = createAvatar(this, 0, 0, 200, you);
        this.nameText.setText(you.alias).setColor(you.color);
        this.titleText.setText(`You're in ${this.session.roomCode}`);
        this.statusText.setText('Waiting for the host to start...');
        this.layout();
    }

    cleanup() {
        this.closed = true;
        this.scale.off('resize', this.layout, this);
        this.session?.off('roster', this.show, this);
        // The session stays open: it carries the player through the game. Quit closes it (see quit()).
    }

    layout() {
        const {width, height} = this.scale;
        const cx = width / 2;
        const cy = height / 2;
        const s = Phaser.Math.Clamp(Math.min(width, height) / 720, 0.5, 1.5);

        this.background.setPosition(cx, cy)
            .setScale(Math.max(width / this.background.width, height / this.background.height));

        const panelW = 560 * s;
        const panelH = 700 * s;
        this.panel.clear()
            .fillStyle(PANEL, 1)
            .fillRoundedRect(cx - panelW / 2, cy - panelH / 2, panelW, panelH, 28 * s)
            .lineStyle(3, 0xffffff, 1)
            .strokeRoundedRect(cx - panelW / 2, cy - panelH / 2, panelW, panelH, 28 * s);

        this.avatar?.setPosition(cx, cy - 170 * s).setScale(s * 1.3);
        this.nameText.setPosition(cx, cy - 20 * s).setFontSize(38 * s);
        this.titleText.setPosition(cx, cy + 50 * s).setFontSize(34 * s);
        this.statusText.setPosition(cx, cy + 130 * s).setFontSize(28 * s).setWordWrapWidth(panelW - 60 * s);
        this.quitBtn.setPosition(cx, cy + 250 * s).setScale(s);
    }

    /** Leave the room for good and go back to the main menu. */
    quit() {
        leaveToMenu(this);
    }
}
