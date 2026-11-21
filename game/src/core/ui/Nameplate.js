import * as Phaser from 'phaser';
import {COLORS, HEX, textStyle} from '../theme.js';

/**
 * A readable name tag: near-white text on a dark pill, with the player's own color as a stripe on the left, so any
 * player color stays legible. Long names are cut with an ellipsis to fit `maxWidth`. `caption` adds a small line
 * under the pill (used for "Reconnecting..."). Returns a container centred on (x, y); the caller sets depth/alpha.
 *
 * `player` is a roster entry ({alias, color}).
 */
export function addNameplate(scene, x, y, player, {maxWidth = 180, fontSize = 20, caption = ''} = {}) {
    const padX = Math.round(fontSize * 0.6);
    const stripe = Math.max(5, Math.round(fontSize * 0.28));
    const height = Math.round(fontSize * 1.6);

    const label = scene.add.text(0, 0, player.alias, textStyle('name', {fontSize: `${fontSize}px`})).setOrigin(0.5);
    const room = maxWidth - 2 * padX - stripe;
    let name = player.alias;
    while (label.width > room && name.length > 1) {
        name = name.slice(0, -1);
        label.setText(`${name.trimEnd()}…`);
    }

    const width = Math.ceil(label.width + 2 * padX + stripe);
    const bg = scene.add.graphics();
    bg.fillStyle(HEX.bgDeep, 0.88).fillRoundedRect(-width / 2, -height / 2, width, height, height / 2);
    bg.lineStyle(2, Phaser.Display.Color.HexStringToColor(player.color).color, 1)
        .strokeRoundedRect(-width / 2, -height / 2, width, height, height / 2);
    bg.fillStyle(Phaser.Display.Color.HexStringToColor(player.color).color, 1)
        .fillCircle(-width / 2 + height / 2 - padX / 2, 0, stripe);
    label.setX(stripe / 2 + padX / 4);

    const parts = [bg, label];
    if (caption) {
        parts.push(scene.add.text(0, height / 2 + fontSize * 0.7, caption,
            textStyle('meta', {fontSize: `${Math.max(14, Math.round(fontSize * 0.7))}px`, color: COLORS.pink, stroke: COLORS.bgDeep, strokeThickness: 3})).setOrigin(0.5));
    }
    return scene.add.container(x, y, parts).setSize(width, height);
}
