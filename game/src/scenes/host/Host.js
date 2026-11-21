import {Scene} from "phaser";
import {HOST_CENTER_X, HOST_CENTER_Y, HOST_HEIGHT, HOST_WIDTH} from "../../constants.js";
import {NetworkManager} from "../../core/network.js";
import {enterFixedSize, fitImageToScreen} from "../../core/scaling.js";
import {applyVolume} from "../../core/hostSettings.js";
import {createTextButton} from "../../core/ui/Button.js";
import {hubErrorMessage} from "../../core/gameHub.js";
import {createPlaceholderAvatar} from "../../core/characters.js";
import {COLORS, HEX, prefersReducedMotion, textStyle} from "../../core/theme.js";
import {GameSession, getSession, leaveToMenu, setSession} from "../../core/session.js";

// Overlay scenes launched on top of the lobby (see scenes/host/overlays).
const OVERLAYS = ['HostSettings', 'QuitConfirm'];

// The marquee plate sits on the plain wood wall under the windows (the checkered trim is above it).
const MARQUEE_Y = 374;
const MARQUEE_H = 80;

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
        this.startPulse = null;
        this.starting = false;
        this.rosterItems = [];
        this.placeholderOf = new Map();
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
        this.buildMarquee();
        this.startBtn = createTextButton(this, 1050, MARQUEE_Y + MARQUEE_H / 2, 'Start Game', () => this.startGame(),
            {width: 250, height: 60, fontSize: '26px', variant: 'primary'});
        this.startBtn.setEnabled(false);
        // Errors (room could not be created, game could not start) show just under the marquee.
        this.statusText = this.add.text(HOST_CENTER_X, MARQUEE_Y + MARQUEE_H + 14, '',
            textStyle('bodyOnArt', {fontSize: '20px', color: COLORS.pink})).setOrigin(0.5);
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

        this.codeText.setText(this.roomCode);

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

    /**
     * The dark plate on the plain wainscot below the windows: join code on the left, how many have joined in the
     * middle, and (set up in create) the Start button on the right. It stays clear of the sign and the windows.
     */
    buildMarquee() {
        const left = 90;
        const width = HOST_WIDTH - 180;
        this.add.graphics()
            .lineStyle(10, HEX.cyan, 0.1).strokeRoundedRect(left, MARQUEE_Y, width, MARQUEE_H, 18)
            .fillStyle(HEX.bgDeep, 0.88).fillRoundedRect(left, MARQUEE_Y, width, MARQUEE_H, 18)
            .lineStyle(2, HEX.cyan, 0.85).strokeRoundedRect(left, MARQUEE_Y, width, MARQUEE_H, 18);

        this.add.text(left + 28, MARQUEE_Y + 18, 'JOIN CODE', textStyle('meta', {fontSize: '16px'})).setOrigin(0, 0.5);
        this.codeText = this.add.text(left + 28, MARQUEE_Y + 52, '- - - -', textStyle('code', {fontSize: '44px'})).setOrigin(0, 0.5);

        this.needText = this.add.text(650, MARQUEE_Y + MARQUEE_H / 2, '', textStyle('instruction', {fontSize: '26px', stroke: undefined, strokeThickness: 0})).setOrigin(0.5);

        // Players X/MAX in the bottom-right corner of the screen.
        const bx = HOST_WIDTH - 24;
        const by = HOST_HEIGHT - 16;
        this.countBg = this.add.graphics()
            .fillStyle(HEX.bgDeep, 0.85).fillRoundedRect(bx - 130, by - 62, 130, 62, 14)
            .lineStyle(2, HEX.cyan, 0.85).strokeRoundedRect(bx - 130, by - 62, 130, 62, 14);
        this.add.text(bx - 65, by - 50, 'PLAYERS', textStyle('meta', {fontSize: '14px'})).setOrigin(0.5);
        this.countText = this.add.text(bx - 65, by - 24, '0/0', textStyle('code', {fontSize: '34px'})).setOrigin(0.5);
    }

    /** A slow pulse on Start while the game can be started. Skipped when the viewer prefers reduced motion. */
    pulseStart(on) {
        if (prefersReducedMotion() || !this.startBtn) return;
        if (on && !this.startPulse) {
            this.startPulse = this.tweens.add({targets: this.startBtn, scale: 1.05, duration: 900, yoyo: true, repeat: -1, ease: 'Sine.easeInOut'});
        } else if (!on && this.startPulse) {
            this.startPulse.remove();
            this.startPulse = null;
            this.startBtn.setScale(1);
        }
    }

    /** How many players are ready, and whether every joined player is. */
    readyStatus() {
        const roster = this.session?.roster ?? [];
        const ready = roster.filter(p => p.ready).length;
        return {ready, total: roster.length, all: roster.length > 0 && ready === roster.length};
    }

    /** The game can start once enough players joined and every one of them is ready. */
    canStart() {
        const {playerCount, minPlayers, phase} = this.state;
        return phase === 'Lobby' && playerCount >= minPlayers && this.readyStatus().all;
    }

    /** "X/MAX" in the corner, the line in the marquee, and whether Start is enabled. */
    updateLobbyStatus() {
        if (!this.needText) return;
        const {playerCount, minPlayers, phase, settings} = this.state;
        this.countText.setText(`${playerCount}/${settings.maxPlayers || '?'}`);
        const {ready, all} = this.readyStatus();
        if (phase !== 'Lobby') this.needText.setText('Game in progress').setColor(COLORS.textMuted);
        else if (playerCount < minPlayers) this.needText.setText(`Need ${minPlayers - playerCount} more to start`).setColor(COLORS.gold);
        else if (all) this.needText.setText('Everyone is ready!').setColor(COLORS.green);
        else this.needText.setText(`${ready} of ${playerCount} ready`).setColor(COLORS.gold);

        const startable = this.canStart();
        this.startBtn?.setEnabled(startable);
        this.pulseStart(startable);
    }

    /** Each player keeps the same silhouette for as long as they are in the lobby (the roster is sorted by name). */
    placeholderIndex(player) {
        if (!this.placeholderOf.has(player.id)) {
            const used = new Set(this.placeholderOf.values());
            let index = 0;
            while (used.has(index)) index++;
            this.placeholderOf.set(player.id, index);
        }
        return this.placeholderOf.get(player.id);
    }

    /**
     * Draws the lineup on the floor: one slot for each seat the room allows, filled by the players who have joined.
     * Seats still open show a dashed circle, so everyone can see how many are expected.
     */
    drawRoster(roster) {
        this.rosterItems.forEach(item => item.destroy());
        this.rosterItems = [];
        // Forget silhouettes of players who are gone, so their pick can be reused.
        const ids = new Set(roster.map(p => p.id));
        for (const id of this.placeholderOf.keys()) if (!ids.has(id)) this.placeholderOf.delete(id);

        const slots = Math.max(this.state?.settings?.maxPlayers ?? 0, roster.length, 1);
        const gap = Math.min(250, (HOST_WIDTH - 180) / slots);
        const size = Math.min(180, gap * 0.82);
        const startX = HOST_CENTER_X - ((slots - 1) * gap) / 2;
        const feet = 640;
        const y = feet - size / 2;

        for (let i = 0; i < slots; i++) {
            const x = startX + i * gap;
            const player = roster[i];
            if (player) {
                if (player.ready) this.rosterItems.push(this.drawReadyRing(x, y, size));
                this.rosterItems.push(createPlaceholderAvatar(this, x, y, size, this.placeholderIndex(player), {dim: !player.connected}));
                this.rosterItems.push(this.drawReadyBadge(x, feet + 28, player.ready));
                if (!player.connected) {
                    this.rosterItems.push(this.add.text(x, feet + 54, 'Reconnecting…',
                        textStyle('meta', {fontSize: '14px', color: COLORS.pink})).setOrigin(0.5));
                }
            } else {
                this.rosterItems.push(this.drawEmptySeat(x, y, size));
            }
        }
        this.updateLobbyStatus();
    }

    /** A green glowing ring behind a ready player. */
    drawReadyRing(x, y, size) {
        const radius = size * 0.44;
        return this.add.graphics().setPosition(x, y)
            .lineStyle(12, HEX.green, 0.18).strokeCircle(0, 0, radius)
            .lineStyle(4, HEX.green, 0.95).strokeCircle(0, 0, radius);
    }

    /** "READY" in a green pill, or a muted "Not ready". */
    drawReadyBadge(x, y, ready) {
        if (!ready) return this.add.text(x, y, 'Not ready', textStyle('meta', {fontSize: '16px', color: COLORS.textMuted})).setOrigin(0.5);
        const label = this.add.text(0, 0, 'READY', textStyle('meta', {fontSize: '16px', color: COLORS.bgDeep})).setOrigin(0.5);
        const w = label.width + 28;
        const bg = this.add.graphics().fillStyle(HEX.green, 1).fillRoundedRect(-w / 2, -13, w, 26, 13);
        return this.add.container(x, y, [bg, label]);
    }

    /** A dashed circle with "Waiting..." for a seat nobody has taken. */
    drawEmptySeat(x, y, size) {
        const radius = size * 0.36;
        const ring = this.add.graphics().setPosition(x, y);
        ring.fillStyle(HEX.bgDeep, 0.35).fillCircle(0, 0, radius);
        ring.lineStyle(3, HEX.cyan, 0.7);
        const segments = 20;
        for (let k = 0; k < segments; k += 2) {
            ring.beginPath();
            ring.arc(0, 0, radius, (k / segments) * Math.PI * 2, ((k + 1) / segments) * Math.PI * 2);
            ring.strokePath();
        }
        const label = this.add.text(0, 0, 'Waiting…', textStyle('bodyOnArt', {fontSize: '18px'})).setOrigin(0.5);
        return this.add.container(0, 0, [ring, label]);
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
        const seatsChanged = this.state?.settings?.maxPlayers !== state.settings?.maxPlayers;
        this.state = state;
        this.updateLobbyStatus();
        // The lineup shows one seat per allowed player, so it is redrawn when the host changes the limit.
        if (seatsChanged && this.session) this.drawRoster(this.session.roster);
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
