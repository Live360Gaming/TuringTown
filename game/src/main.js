import {ensureLatestBuild} from './core/version.js';
import { Boot } from './scenes/boot/Boot.js';
import { Preloader } from "./scenes/boot/Preloader.js";
import { Game } from 'phaser';
import { MainMenu } from "./scenes/menu/MainMenu.js";
import * as Phaser from 'phaser';
import {PLAYER_HEIGHT, PLAYER_WIDTH} from "./constants.js";
import {Host} from "./scenes/host/Host.js";
import {Player} from "./scenes/player/Player.js";
import {PlayerLobby} from "./scenes/player/PlayerLobby.js";
import {TURING_TOWN_SCENES} from "./minigames/turing-town/index.js";
import {WITTY_BITS_SCENES} from "./minigames/witty-bits/index.js";
import {SHOUT_OUT_SCENES} from "./minigames/shout-out/index.js";
import {GAME_NIGHT_SCENES} from "./minigames/game-night/index.js";
import {HostSettings} from "./scenes/host/overlays/HostSettings.js";
import {QuitConfirm} from "./scenes/host/overlays/QuitConfirm.js";
import {PresenceToast} from "./scenes/host/overlays/PresenceToast.js";
import {installTextDefaults} from "./core/theme.js";
import {loadSeason} from "./core/season.js";

installTextDefaults();

const config = {
    type: Phaser.AUTO,
    // Start with PLAYER size
    width: PLAYER_WIDTH,
    height: PLAYER_HEIGHT,
    parent: 'game-container',
    // Lets scenes use real HTML elements (e.g. the room code input)
    dom: {createContainer: true},
    backgroundColor: 'black',
    // Boot, loading and menu screens fill the whole window; Host switches to a fixed 16:9 FIT.
    scale: {
        mode: Phaser.Scale.RESIZE,
        autoCenter: Phaser.Scale.CENTER_BOTH
    },
    scene: [
        Boot,
        Preloader,
        MainMenu,
        Host,
        Player,
        PlayerLobby,
        // The night's own screens (Planning, Scoreboard, Finale) and each game's screens, one per stage; the director
        // starts them as the server moves the night along.
        ...GAME_NIGHT_SCENES,
        ...TURING_TOWN_SCENES,
        ...WITTY_BITS_SCENES,
        ...SHOUT_OUT_SCENES,
        // Overlays come after Host so they draw on top of it.
        HostSettings,
        QuitConfirm,
        // Last, so the disconnect banners draw above every host screen and overlay.
        PresenceToast,
    ]
};

// The server picks the season before any art is loaded (fast fallback to the normal look if it does not answer).
export default ensureLatestBuild().then(loadSeason).then(() => new Game(config));
