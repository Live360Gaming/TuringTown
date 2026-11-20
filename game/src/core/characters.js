import {getSession} from './session.js';

/**
 * A player's avatar: their character's sprite, or a robot if they have been unmasked as a bot.
 * `player` is a roster entry or a chat message ({id | playerId, characterId}). Returns a container.
 */
export function createAvatar(scene, x, y, size, player, {dim = false} = {}) {
    const key = getSession(scene)?.spriteKey(player) ?? player.characterId;
    const sprite = scene.add.image(0, 0, key).setDisplaySize(size, size);
    const avatar = scene.add.container(x, y, [sprite]).setSize(size, size);
    if (dim) avatar.setAlpha(0.35);
    return avatar;
}
