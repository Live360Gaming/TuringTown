import * as Phaser from 'phaser';
import {connectGameHub} from './gameHub.js';
import {GameDirector} from './director.js';
import {ROBOT_KEYS} from '../constants.js';
import {getMusic} from './music.js';
import {followStageMusic} from '../minigames/stageMusic.js';

const REGISTRY_KEY = 'session';
/** A typing indicator with no update for this long is dropped (a phone that went away mid-sentence). */
const TYPING_EXPIRY_MS = 30000;
/** A message the main menu shows once, e.g. the goodbye when the host closes the room. */
export const MENU_NOTICE_KEY = 'menuNotice';

/**
 * One player's or the host's live connection to a room, and everything the server has told them about it.
 * It outlives scenes: scenes read it and listen to it, but never create or stop the connection.
 *
 * Events: 'state' (state), 'roster' (roster), 'chat' (message), 'typing' (Set of player ids), 'progress' ({done, total}), and
 * 'presence' ({player, status}) with status 'disconnected' | 'reconnected' | 'removed' when someone drops, comes
 * back, or is cleared from the lobby.
 *
 * The server is the only source of truth. The state is always a full snapshot, so a screen can render from
 * `session.state` alone, whether it was open when the stage began or opened late (a refreshed phone).
 */
export class GameSession extends Phaser.Events.EventEmitter {
    constructor(game, {role, roomCode, lobbyKey, hostToken = null, playerId = null, playerToken = null}) {
        super();
        this.game = game;
        this.role = role; // 'host' | 'player'
        this.roomCode = roomCode;
        this.lobbyKey = lobbyKey;
        this.hostToken = hostToken;
        this.playerId = playerId;
        this.playerToken = playerToken;

        this.state = null;
        this.roster = [];
        this.transcript = [];
        // Players who are typing right now (humans and bots alike), id -> expiry timer.
        this.typing = new Map();
        this.you = null;
        this.progress = {done: 0, total: 0};
        this.gameError = null;
        this.clockOffsetMs = 0;
        // Ids of players who have been revealed as bots. Empty until a bot is found.
        this.unmasked = new Set();

        this.connection = null;
        this.director = null;
        this.closed = false;
    }

    /** Connects and joins the room. Resolves once `state`, `roster` and `you` are filled in. */
    async connect() {
        const {connection} = await connectGameHub({
            // Runs again after an automatic reconnect, which refreshes everything below.
            join: async c => {
                const result = this.role === 'host'
                    ? await c.invoke('JoinAsHost', this.roomCode, this.hostToken)
                    : await c.invoke('JoinAsPlayer', this.roomCode, this.playerId, this.playerToken);
                this.applyJoin(result);
                return result;
            },
            on: {
                StateChanged: state => this.setState(state),
                RosterChanged: roster => this.setRoster(roster),
                Typing: ({playerId, typing}) => this.setTypingFor(playerId, typing),
                ChatMessage: message => {
                    this.transcript.push(message);
                    this.setTypingFor(message.playerId, false);
                    this.emit('chat', message);
                },
                RoomClosed: reason => this.onRoomClosed(reason),
                GameError: message => {
                    this.gameError = message;
                    this.emit('gameError', message);
                },
                Progress: progress => {
                    this.progress = progress;
                    this.emit('progress', progress);
                },
            },
        });

        if (this.closed) {
            connection.stop();
            return;
        }
        this.connection = connection;
    }

    /**
     * The host or the leader (the first player) quit, so the room is gone. Everyone else is told goodbye on the menu;
     * whoever quit already left on their own.
     */
    onRoomClosed(reason) {
        if (this.closed) return;
        const game = this.game;
        closeSession(game);
        clearCredentials();
        const who = reason === 'leader' ? 'The first player has left' : 'The host has left';
        game.registry.set(MENU_NOTICE_KEY, `${who}, so the game is over.
Thanks for playing. Goodbye!`);
        game.scene.getScenes(true).forEach(scene => game.scene.stop(scene.scene.key));
        game.scene.start('MainMenu');
    }

    /** Starts moving the screen along with the game. Call once, after connect(). */
    attachDirector() {
        this.director?.stop();
        this.director = new GameDirector(this.game, this, this.lobbyKey);
        this.director.start();
        // The TV shows a banner whenever someone drops, on every screen (see scenes/host/overlays/PresenceToast).
        if (this.role === 'host') {
            this.game.scene.run('PresenceToast');
            // Music plays on the TV only, and follows the stage.
            this.stopMusic?.();
            this.stopMusic = followStageMusic(this.game, this);
        }
    }

    applyJoin(result) {
        // The server's clock minus ours, so deadlines can be shown correctly even if the device clock is off.
        this.clockOffsetMs = result.serverTimeUtcMs - Date.now();
        this.you = result.you;
        this.transcript = result.transcript ?? [];
        // Bots already found out before this client (re)joined.
        this.unmasked = new Set(result.unmasked ?? []);
        this.roster = result.roster;
        this.emit('roster', this.roster);
        this.setState(result.state);
    }

    setState(state) {
        if (this.state?.stage !== state.stage) this.clearTyping();
        if (state.stage === 'Tutorial') this.gameError = null;
        this.state = state;
        this.progress = {done: 0, total: 0};

        if (state.stage === 'Lobby') {
            // A new game gets a new seating order, and nobody has been found out yet.
            this.seatOrder = null;
            this.unmasked.clear();
        } else if (state.stage === 'Reveal') {
            // The first moment a bot is known: the one just voted out, if it was a bot.
            if (state.data?.wasBot && state.data.eliminated) this.unmasked.add(state.data.eliminated.id);
        } else if (state.stage === 'Results' || state.stage === 'Finale') {
            // Turing Town's results and the night's finale show every role, so every bot is shown from then on.
            state.data?.roles?.forEach(role => {
                if (role.wasBot) this.unmasked.add(role.playerId);
            });
        }

        this.emit('state', state);
    }

    /**
     * Which image to draw for a player: their character, or, once they have been found out as a bot, a robot.
     * Until then a bot looks exactly like a human (the server never tells us), which is the point of the game.
     * `player` can be a roster entry ({id}) or a chat message ({playerId}).
     */
    spriteKey(player) {
        const id = player.id ?? player.playerId;
        if (!this.unmasked.has(id)) return player.characterId;

        // The same robot every time for the same player, spread over the robot art.
        let hash = 0;
        for (const ch of id) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
        return ROBOT_KEYS[hash % ROBOT_KEYS.length];
    }

    /**
     * Everyone in the room in a random order that stays the same for the whole game, so the characters keep
     * their seats from screen to screen. (The roster itself is sorted by alias, which would be the same every game.)
     */
    seatedRoster() {
        const byId = new Map(this.roster.map(player => [player.id, player]));
        this.seatOrder = (this.seatOrder ?? []).filter(id => byId.has(id));

        const seated = new Set(this.seatOrder);
        const newcomers = this.roster.filter(player => !seated.has(player.id)).map(player => player.id);
        this.seatOrder.push(...Phaser.Utils.Array.Shuffle(newcomers));

        return this.seatOrder.map(id => byId.get(id));
    }

    setRoster(roster) {
        const previous = this.roster;
        this.roster = roster;
        // The player's own entry carries whether they are still in the game.
        if (this.you) this.you = roster.find(p => p.id === this.you.id) ?? this.you;
        this.emit('roster', roster);
        this.emitPresence(previous, roster);
    }

    /** Tells listeners who dropped, came back, or was cleared between two rosters. */
    emitPresence(previous, roster) {
        const now = new Map(roster.map(p => [p.id, p]));
        for (const before of previous) {
            const after = now.get(before.id);
            if (!after) this.emit('presence', {player: before, status: 'removed'});
            else if (before.connected && !after.connected) this.emit('presence', {player: after, status: 'disconnected'});
            else if (!before.connected && after.connected) this.emit('presence', {player: after, status: 'reconnected'});
        }
    }

    /**
     * Marks a player as typing (or not) and emits 'typing' with the Set of player ids. The server only says when someone
     * starts and stops, so a flag that is never cleared (a phone that went away) expires on its own.
     */
    setTypingFor(playerId, typing) {
        clearTimeout(this.typing.get(playerId));
        const had = this.typing.has(playerId);
        if (typing) this.typing.set(playerId, setTimeout(() => this.setTypingFor(playerId, false), TYPING_EXPIRY_MS));
        else this.typing.delete(playerId);
        if (had !== typing) this.emit('typing', new Set(this.typing.keys()));
    }

    clearTyping() {
        if (this.typing.size === 0) return;
        this.typing.forEach(timer => clearTimeout(timer));
        this.typing.clear();
        this.emit('typing', new Set());
    }

    /** Tells the room this player started or stopped typing. Best effort: a failure just means no indicator. */
    reportTyping(typing) {
        this.invoke('SetTyping', typing).catch(() => {});
    }

    /** Calls a hub method. Rejects with the server's message if it refuses (see hubErrorMessage). */
    invoke(method, ...args) {
        if (!this.connection) return Promise.reject(new Error('Not connected.'));
        return this.connection.invoke(method, ...args);
    }

    /** The server's current time in ms (our clock plus the offset). */
    serverNow() {
        return Date.now() + this.clockOffsetMs;
    }

    /** Milliseconds left in the current stage, or null if the stage has no deadline. */
    msLeft() {
        const ends = this.state?.endsAtUtcMs;
        return ends == null ? null : Math.max(0, ends - this.serverNow());
    }

    /** True when this player has been voted out and can only watch. */
    get spectating() {
        return this.role === 'player' && this.you != null && !this.you.alive;
    }

    close() {
        if (this.closed) return;
        this.closed = true;
        this.director?.stop();
        if (this.role === 'host') {
            this.game.scene.stop('PresenceToast');
            this.stopMusic?.();
            getMusic(this.game).stop(400);
        }
        const connection = this.connection;
        this.connection = null;
        connection?.stop();
        this.removeAllListeners();
    }
}

export function getSession(scene) {
    return scene.game.registry.get(REGISTRY_KEY) ?? null;
}

/** Makes `session` the current one, closing any session left over from before. */
export function setSession(game, session) {
    closeSession(game);
    game.registry.set(REGISTRY_KEY, session);
}

export function closeSession(game) {
    const session = game.registry.get(REGISTRY_KEY);
    if (!session) return;
    game.registry.remove(REGISTRY_KEY);
    session.close();
}

// A player's credentials are kept for the life of the browser tab, so refreshing the page mid-game rejoins the
// same seat instead of dropping out. (sessionStorage can throw, e.g. in private windows; rejoining is then off.)
const CREDENTIALS_KEY = 'turingtown.player';

/** Remember {roomCode, playerId, playerToken} so a refresh can rejoin. */
export function saveCredentials(credentials) {
    try {
        sessionStorage.setItem(CREDENTIALS_KEY, JSON.stringify(credentials));
    } catch {
        // No storage: a refresh simply returns to the menu.
    }
}

export function loadCredentials() {
    try {
        const raw = sessionStorage.getItem(CREDENTIALS_KEY);
        return raw ? JSON.parse(raw) : null;
    } catch {
        return null;
    }
}

export function clearCredentials() {
    try {
        sessionStorage.removeItem(CREDENTIALS_KEY);
    } catch {
        // Nothing stored.
    }
}

/** Leave the room for good and go back to the main menu. */
export function leaveToMenu(scene) {
    const game = scene.game;
    const session = getSession(scene);
    // The host or the leader (the first player) quitting closes the room, so the others are told goodbye instead of
    // being left hanging. The server ignores the call from anyone else.
    const closing = (session?.role === 'host' || session?.you?.leader) && session.connection
        ? Promise.race([session.invoke('CloseRoom').catch(() => {}), new Promise(resolve => setTimeout(resolve, 1500))])
        : Promise.resolve();

    closing.then(() => {
        closeSession(game);
        clearCredentials();
        game.scene.getScenes(true).forEach(s => game.scene.stop(s.scene.key));
        game.scene.start('MainMenu');
    });
}
