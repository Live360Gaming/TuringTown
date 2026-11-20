// Moves the screen along as the server moves the game from stage to stage.
//
// There is no registry: a stage's scene is found by name, `<GameId><Role><Stage>`, for example
// "TuringTownHostDiscussion" or "TuringTownPlayerVoting". A game just has to register scenes with those keys.
// The lobby is the one screen that is not part of a game, so its key is given when the director is created.

const pascal = id => id.split('-').map(part => part.charAt(0).toUpperCase() + part.slice(1)).join('');

export class GameDirector {
    /**
     * @param game     the Phaser game
     * @param session  the GameSession whose `state` events drive the screens
     * @param lobbyKey scene to show while the room is in the Lobby stage ('Host' or 'PlayerLobby')
     */
    constructor(game, session, lobbyKey) {
        this.game = game;
        this.session = session;
        this.lobbyKey = lobbyKey;
        // The screen that started the session is the lobby.
        this.current = lobbyKey;
    }

    start() {
        this.session.on('state', this.onState, this);
        // The session may already be past the lobby (a phone that refreshed mid-game).
        if (this.session.state) this.onState(this.session.state);
    }

    stop() {
        this.session.off('state', this.onState, this);
    }

    sceneKeyFor(state) {
        if (state.stage === 'Lobby') return this.lobbyKey;
        return `${pascal(state.gameId)}${pascal(this.session.role)}${state.stage}`;
    }

    onState(state) {
        const key = this.sceneKeyFor(state);
        // Same screen: it keeps itself up to date from the session's events (e.g. the tutorial step).
        if (key === this.current) return;

        if (!this.game.scene.getScene(key)) {
            console.warn(`No scene "${key}" for stage ${state.stage}`);
            return;
        }

        const previous = this.current;
        this.current = key;
        this.game.scene.stop(previous);
        this.game.scene.start(key);
    }
}
