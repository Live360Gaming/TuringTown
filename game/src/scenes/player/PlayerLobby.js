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
// A tap that moves less than this is a pick; more is a drag of the picker strip.
const DRAG_SLOP = 8;

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
        this.picker = {mode: 'grid', scroll: 0, maxScroll: 0, cell: 0, view: null, drag: null, centred: false};
        // The tiles live in one container so the strip can scroll and be clipped; a single zone over the picker
        // takes the taps and drags, so a tile hidden behind the clip can never be picked.
        this.pickerStrip = this.add.container(0, 0);
        this.pickerClip = this.make.graphics({}, false);
        this.pickerZone = this.add.zone(0, 0, 1, 1).setOrigin(0, 0).setInteractive({useHandCursor: true});
        this.pickerZone.on('pointerdown', this.pickerDown, this);
        this.input.on('pointermove', this.pickerMove, this);
        this.input.on('pointerup', this.pickerUp, this);
        this.input.on('wheel', this.pickerWheel, this);
        this.pickerPrev = createTextButton(this, 0, 0, '‹', () => this.scrollPicker(-1), {width: 44, height: 70, fontSize: '40px', variant: 'ghost'});
        this.pickerNext = createTextButton(this, 0, 0, '›', () => this.scrollPicker(1), {width: 44, height: 70, fontSize: '40px', variant: 'ghost'});
        this.pickerPrev.setVisible(false);
        this.pickerNext.setVisible(false);
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
            this.applyRoom(this.session.room);
            this.show();
            // A refresh, or a return after a game, may already be past the lobby; the director has moved us on if so.
            this.session.on('roster', this.show, this);
            this.session.on('room', this.applyRoom, this);
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
        this.refreshPicker();
        this.updateReady();
        this.layout();
    }

    /** The room's limits, pushed by the server: the leader's Start button needs the minimum number of players. */
    applyRoom(room) {
        if (!room || this.closed) return;
        this.minPlayers = room.minPlayers ?? 1;
        this.updateReady();
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
        const ready = !this.session.you.ready;
        if (ready) this.sound.play('ready_sfx');
        try {
            await this.session.invoke('SetReady', ready);
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

    /** One tile per character, made once; refreshPicker() keeps them up to date with the roster. */
    buildPicker() {
        for (const key of CHARACTER_KEYS) {
            const img = this.add.image(0, 0, key).setDisplaySize(PICK_SIZE, PICK_SIZE);
            const ring = this.add.graphics();
            const tile = this.add.container(0, 0, [img, ring]).setSize(PICK_SIZE, PICK_SIZE);
            tile.key = key;
            tile.ring = ring;
            this.pickerStrip.add(tile);
            this.pickerTiles.push(tile);
        }
    }

    /** Yours is outlined, ones other players hold are dimmed, the rest can be tapped. */
    refreshPicker() {
        if (this.pickerTiles.length === 0) this.buildPicker();
        const you = this.session.you;
        const takenByOthers = new Set(this.session.roster.filter(p => p.id !== you.id).map(p => p.characterId));
        for (const tile of this.pickerTiles) {
            const mine = tile.key === you.characterId;
            tile.mine = mine;
            tile.taken = takenByOthers.has(tile.key);
            tile.ring.clear()
                .lineStyle(mine ? 5 : 2, mine ? HEX.cyan : HEX.border, 1)
                .strokeRoundedRect(-PICK_SIZE / 2, -PICK_SIZE / 2, PICK_SIZE, PICK_SIZE, 10);
            tile.setAlpha(tile.taken ? 0.3 : 1);
        }
    }

    /** The tile under a screen point, or null (also null for a point the strip's clip hides). */
    tileAt(x, y) {
        const {cell, view} = this.picker;
        if (view && (x < view.x || x > view.x + view.w)) return null;
        return this.pickerTiles.find(t =>
            Math.abs(x - (this.pickerStrip.x + t.x)) <= cell / 2 && Math.abs(y - t.y) <= cell / 2) ?? null;
    }

    pickerDown(pointer) {
        this.picker.drag = {startX: pointer.x, startScroll: this.picker.scroll, moved: false};
    }

    pickerMove(pointer) {
        const drag = this.picker.drag;
        if (!drag || !pointer.isDown) return;
        const dx = pointer.x - drag.startX;
        if (Math.abs(dx) > DRAG_SLOP) drag.moved = true;
        if (drag.moved && this.picker.mode === 'strip') this.setPickerScroll(drag.startScroll - dx);
    }

    pickerUp(pointer) {
        const drag = this.picker.drag;
        this.picker.drag = null;
        if (!drag || drag.moved) return;
        const tile = this.tileAt(pointer.x, pointer.y);
        if (tile && !tile.mine && !tile.taken) this.pick(tile.key);
    }

    pickerWheel(pointer, objects, dx, dy) {
        if (this.picker.mode !== 'strip') return;
        this.setPickerScroll(this.picker.scroll + (Math.abs(dx) > Math.abs(dy) ? dx : dy));
    }

    /** The arrows move the strip by a view's width less one tile, so one tile stays in sight. */
    scrollPicker(direction) {
        const {view, cell} = this.picker;
        if (!view) return;
        const step = Math.max(cell, view.w - cell);
        this.setPickerScroll(this.picker.scroll + direction * step, true);
    }

    setPickerScroll(value, animate = false) {
        const scroll = Phaser.Math.Clamp(value, 0, this.picker.maxScroll);
        this.picker.scroll = scroll;
        const x = this.picker.view ? -scroll : 0;
        this.tweens.killTweensOf(this.pickerStrip);
        if (animate) this.tweens.add({targets: this.pickerStrip, x, duration: 220, ease: 'Cubic.easeOut'});
        else this.pickerStrip.x = x;
        this.updatePickerArrows();
    }

    updatePickerArrows() {
        const strip = this.picker.mode === 'strip';
        this.pickerPrev.setVisible(strip);
        this.pickerNext.setVisible(strip);
        this.pickerPrev.setEnabled(this.picker.scroll > 0);
        this.pickerNext.setEnabled(this.picker.scroll < this.picker.maxScroll);
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
        this.input.off('pointermove', this.pickerMove, this);
        this.input.off('pointerup', this.pickerUp, this);
        this.input.off('wheel', this.pickerWheel, this);
        this.pickerClip.destroy();
        this.session?.off('roster', this.show, this);
        this.session?.off('room', this.applyRoom, this);
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

        const pickerBottom = this.layoutPicker(cx, cy - 60 * v, panelW, availH, s);

        const statusY = pickerBottom + 36 * s;
        this.statusText.setPosition(cx, statusY)
            .setFontSize(fontPx(28, s, 16)).setWordWrapWidth(panelW - 60 * s);
        const readyY = Math.max(cy + 215 * v, statusY + 74 * s);
        this.readyBtn.setPosition(cx, readyY).setScale(Math.max(s, 0.6));
        this.startBtn.setPosition(cx, readyY + 70 * v).setScale(Math.max(s, 0.6));
        this.quitBtn.setPosition(cx, cy + panelH / 2 - 60 * s).setScale(Math.max(s, 0.6));
    }

    /**
     * The character picker, centred on row y. Up to two rows it is a grid; with more characters than two rows hold it
     * becomes one row that scrolls (drag, wheel or the arrows), so it never pushes the buttons off the card however
     * many characters there are. Returns the picker's bottom edge.
     */
    layoutPicker(cx, y, panelW, availH, s) {
        const count = this.pickerTiles.length;
        const picker = this.picker;
        if (count === 0) return y;
        const innerW = panelW - 40 * s;
        const minCell = Phaser.Math.Clamp(96 * s, 64, 110);
        const maxCols = Math.max(3, Math.floor(innerW / minCell));
        const grid = count <= maxCols * 2;
        picker.mode = grid ? 'grid' : 'strip';

        if (grid) {
            // One row if they fit, else two balanced rows (7 is 4 + 3); a short last row is centred.
            const cols = count <= maxCols ? count : Math.ceil(count / 2);
            const cell = Math.min(110, innerW / Math.max(cols, 3), availH * 0.14);
            const rows = Math.ceil(count / cols);
            const scale = (cell - 10) / PICK_SIZE;
            this.pickerTiles.forEach((tile, i) => {
                const row = Math.floor(i / cols);
                const inRow = Math.min(cols, count - row * cols);
                tile.setPosition(cx + ((i % cols) - (inRow - 1) / 2) * cell, y + row * cell).setScale(scale);
            });
            picker.cell = cell;
            picker.view = null;
            picker.maxScroll = 0;
            this.pickerStrip.clearMask();
            this.placePickerZone(cx - (cols * cell) / 2, y - cell / 2, cols * cell, rows * cell);
            this.setPickerScroll(0);
            return y + (rows - 1) * cell + cell / 2;
        }

        const cell = Math.min(110, Math.max(minCell, availH * 0.12));
        const arrowScale = Math.max(s, 0.6);
        const arrowW = 44 * arrowScale;
        const view = {x: cx - innerW / 2 + arrowW, w: innerW - 2 * arrowW};
        // Show whole tiles: the view is a whole number of cells wide, centred between the arrows.
        const visible = Math.max(1, Math.floor(view.w / cell));
        view.x += (view.w - visible * cell) / 2;
        view.w = visible * cell;
        const scale = (cell - 10) / PICK_SIZE;
        this.pickerTiles.forEach((tile, i) => tile.setPosition(view.x + cell / 2 + i * cell, y).setScale(scale));

        picker.cell = cell;
        picker.view = view;
        picker.maxScroll = Math.max(0, count * cell - view.w);
        this.pickerClip.clear().fillStyle(0xffffff).fillRect(view.x, y - cell / 2, view.w, cell);
        this.pickerStrip.setMask(this.pickerClip.createGeometryMask());
        this.placePickerZone(view.x, y - cell / 2, view.w, cell);
        this.pickerPrev.setPosition(view.x - arrowW / 2 - 4, y).setScale(arrowScale);
        this.pickerNext.setPosition(view.x + view.w + arrowW / 2 + 4, y).setScale(arrowScale);

        // The first time, bring your own character into view.
        let scroll = picker.scroll;
        if (!picker.centred && this.session?.you) {
            const mine = this.pickerTiles.findIndex(t => t.key === this.session.you.characterId);
            if (mine >= 0) {
                scroll = mine * cell - (view.w - cell) / 2;
                picker.centred = true;
            }
        }
        this.setPickerScroll(scroll);
        return y + cell / 2;
    }

    placePickerZone(x, y, w, h) {
        this.pickerZone.setPosition(x, y).setSize(w, h);
        this.pickerZone.input.hitArea.setTo(0, 0, w, h);
    }

    /** Leave the room for good and go back to the main menu. */
    quit() {
        leaveToMenu(this);
    }
}
