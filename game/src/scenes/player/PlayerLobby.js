import * as Phaser from "phaser";
import {Scene} from "phaser";
import {enterFullWindow, safeInsets} from "../../core/scaling.js";
import {createTextButton} from "../../core/ui/Button.js";
import {drawPanel} from "../../core/ui/Panel.js";
import {COLORS, fontPx, HEX, readableOn} from "../../core/theme.js";
import {hubErrorMessage} from "../../core/gameHub.js";
import {createAvatar} from "../../core/characters.js";
import {CHARACTER_KEYS} from "../../constants.js";
import {clearCredentials, GameSession, getSession, leaveToMenu, setSession} from "../../core/session.js";

// Waiting screen: the player has joined and sees the alias and character the server gave them until the host
// starts the game. It also keeps the GameSession for the whole visit; after a game ends the director brings the
// player back here and the same session is reused (see core/director.js).
const PICK_SIZE = 76;
const PICK_GAP = 8;

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
        this.pickerTiles = [];
        this.nameText = this.add.text(0, 0, '', {color: '#ffffff'}).setOrigin(0.5).setResolution(window.devicePixelRatio);
        this.titleText = this.add.text(0, 0, '', {color: '#ffffff'}).setOrigin(0.5).setResolution(window.devicePixelRatio);
        this.statusText = this.add.text(0, 0, 'Connecting...', {color: '#ffffff', align: 'center'}).setOrigin(0.5).setResolution(window.devicePixelRatio);
        this.readyBtn = createTextButton(this, 0, 0, 'Ready', () => this.toggleReady(), {width: 360, height: 70, fontSize: '36px', variant: 'primary'});
        // Only the first player to join (the leader) sees this one.
        this.startBtn = createTextButton(this, 0, 0, 'Start Game', () => this.startGame(), {width: 360, height: 70, fontSize: '36px'});
        this.startBtn.setVisible(false);
        this.minPlayers = 1;
        this.quitBtn = createTextButton(this, 0, 0, 'Quit', () => this.quit(), {width: 360, height: 80, fontSize: '40px', variant: 'ghost'});

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
            this.loadMinPlayers();
            this.show();
            // A refresh, or a return after a game, may already be past the lobby; the director has moved us on if so.
            this.session.on('roster', this.show, this);
            this.session.on('gameError', this.showGameError, this);
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
        this.nameText.setText(you.alias).setColor(readableOn(you.color));
        this.titleText.setText(`You're in ${this.session.roomCode}`);
        this.buildPicker();
        this.updateReady();
        this.layout();
    }

    /** The number of players needed to start, so the leader's Start button knows when it can work. */
    async loadMinPlayers() {
        try {
            const res = await fetch(`${window.location.origin}/api/room/${encodeURIComponent(this.session.roomCode)}`);
            const state = await res.json();
            if (this.closed) return;
            this.minPlayers = state.minPlayers ?? 1;
            this.updateReady();
        } catch {
            // The server still checks the minimum when Start is pressed.
        }
    }

    /** The Ready toggle, the leader's Start button, and the line telling everyone what we are waiting for. */
    updateReady() {
        const you = this.session?.you;
        if (!you || this.closed) return;

        const roster = this.session.roster;
        const waiting = roster.filter(p => !p.ready).length;
        const all = roster.length > 0 && waiting === 0;
        const enough = roster.length >= this.minPlayers;

        this.readyBtn.setLabel(you.ready ? 'Not ready' : 'Ready');
        this.startBtn.setVisible(!!you.leader);
        this.startBtn.setEnabled(all && enough);

        let text;
        if (!enough) text = `Waiting for more players (${roster.length}/${this.minPlayers})...`;
        else if (all) text = you.leader ? 'Everyone is ready. Start when you like!' : 'Everyone is ready. Waiting for the host to start...';
        else text = `Waiting for ${waiting} ${waiting === 1 ? 'player' : 'players'} to get ready...`;
        this.statusText.setText(this.session.gameError || text);
    }

    showGameError(message) {
        this.statusText.setText(message);
    }

    async toggleReady() {
        try {
            await this.session.invoke('SetReady', !this.session.you.ready);
        } catch (err) {
            if (!this.closed) this.statusText.setText(hubErrorMessage(err));
        }
    }

    async startGame() {
        try {
            await this.session.invoke('StartGame');
        } catch (err) {
            if (!this.closed) this.statusText.setText(hubErrorMessage(err));
        }
    }

    /** One tile per character: yours is outlined, ones other players hold are dimmed, the rest can be tapped. */
    buildPicker() {
        this.pickerTiles.forEach(tile => tile.destroy());
        this.pickerTiles = [];

        const you = this.session.you;
        const takenByOthers = new Set(this.session.roster.filter(p => p.id !== you.id).map(p => p.characterId));

        for (const key of CHARACTER_KEYS) {
            const mine = key === you.characterId;
            const taken = takenByOthers.has(key);
            const img = this.add.image(0, 0, key).setDisplaySize(PICK_SIZE, PICK_SIZE);
            const ring = this.add.graphics()
                .lineStyle(mine ? 5 : 2, mine ? HEX.cyan : HEX.border, 1)
                .strokeRoundedRect(-PICK_SIZE / 2, -PICK_SIZE / 2, PICK_SIZE, PICK_SIZE, 10);
            const tile = this.add.container(0, 0, [img, ring]).setSize(PICK_SIZE, PICK_SIZE);
            tile.setAlpha(taken ? 0.3 : 1);
            if (!mine && !taken) {
                tile.setInteractive({useHandCursor: true}).on('pointerup', () => this.pick(key));
            }
            this.pickerTiles.push(tile);
        }
    }

    async pick(characterId) {
        try {
            await this.session.invoke('ChangeCharacter', characterId);
        } catch (err) {
            // Most likely someone grabbed it first; the roster update that follows dims it.
            if (!this.closed) this.statusText.setText(hubErrorMessage(err));
        }
    }

    cleanup() {
        this.closed = true;
        this.scale.off('resize', this.layout, this);
        this.session?.off('roster', this.show, this);
        this.session?.off('gameError', this.showGameError, this);
        // The session stays open: it carries the player through the game. Quit closes it (see quit()).
    }

    layout() {
        const {width, height} = this.scale;
        const safe = safeInsets();
        const cx = width / 2;
        const availH = height - 32 - safe.top - safe.bottom;
        const cy = safe.top + 16 + availH / 2;
        // Sizes follow the card's width; vertical spacing also uses spare screen height on tall phones.
        const s = Phaser.Math.Clamp(Math.min((width - 32 - safe.left - safe.right) / 560, availH / 840), 0.5, 1.5);
        const v = Phaser.Math.Clamp(Math.max(s, availH / 840), s, 1.4);

        this.background.setPosition(width / 2, height / 2)
            .setScale(Math.max(width / this.background.width, height / this.background.height));

        const panelW = 560 * s;
        const panelH = 840 * v;
        drawPanel(this.panel, cx - panelW / 2, cy - panelH / 2, panelW, panelH, s);

        this.avatar?.setPosition(cx, cy - 300 * v).setScale(s * 1.1);
        this.nameText.setPosition(cx, cy - 190 * v).setFontSize(fontPx(38, s, 22));
        this.titleText.setPosition(cx, cy - 150 * v).setFontSize(fontPx(34, s, 18));

        // The character picker: two rows of three, each tile big enough to tap.
        const cell = Math.min(110, (panelW - 40) / 3);
        const tileScale = (cell - 10) / PICK_SIZE;
        const row1 = cy - 60 * v;
        this.pickerTiles.forEach((tile, i) => {
            tile.setPosition(cx + ((i % 3) - 1) * cell, row1 + Math.floor(i / 3) * cell).setScale(tileScale);
        });

        this.statusText.setPosition(cx, row1 + cell * 1.5 + 36 * s)
            .setFontSize(fontPx(28, s, 16)).setWordWrapWidth(panelW - 60 * s);
        this.readyBtn.setPosition(cx, cy + 215 * v).setScale(Math.max(s, 0.6));
        this.startBtn.setPosition(cx, cy + 285 * v).setScale(Math.max(s, 0.6));
        this.quitBtn.setPosition(cx, cy + panelH / 2 - 60 * s).setScale(Math.max(s, 0.6));
    }

    /** Leave the room for good and go back to the main menu. */
    quit() {
        leaveToMenu(this);
    }
}
