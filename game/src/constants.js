// The reason these constants are separate is in case we want to change the resolution of the app to 1080p later or
// something, I guess it's just in case.

// Landscape
export const HOST_WIDTH = 1280;
export const HOST_HEIGHT = 720;

export const HOST_CENTER_X = HOST_WIDTH / 2;
export const HOST_CENTER_Y = HOST_HEIGHT / 2;

// Portrait
export const PLAYER_WIDTH = 720;
export const PLAYER_HEIGHT = 1280;

export const PLAYER_CENTER_X = PLAYER_WIDTH / 2;
export const PLAYER_CENTER_Y = PLAYER_HEIGHT / 2;


// Image keys for the characters in assets/characters/ (512x512 transparent PNGs, loaded by the Preloader).
// CHARACTER_KEYS are the human-looking characters. The server hands these out (Models/Characters.cs lists the
// same keys), to humans and bots alike, so nobody can tell a bot from how it looks.
// ROBOT_KEYS are only ever shown once a bot has been found out (see GameSession.spriteKey).
export const CHARACTER_KEYS = Array.from({length: 7}, (_, i) => `character_${String(i + 1).padStart(2, '0')}`);
export const ROBOT_KEYS = Array.from({length: 3}, (_, i) => `robot_${String(i + 1).padStart(2, '0')}`);

// The host lobby shows these mystery silhouettes instead of a joined player's real character.
// The robot placeholder is never handed to a player slot: a robot next to someone would hint they are a bot.
export const PLACEHOLDER_KEYS = Array.from({length: 9}, (_, i) => `placeholder_${String(i + 1).padStart(2, '0')}`);
export const PLACEHOLDER_ROBOT_KEY = 'placeholder_robot';
