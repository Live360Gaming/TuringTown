import {PANEL} from './Button.js';

/**
 * Draws the rounded card the player screens sit on. `s` is the window's scale factor, so the corner and border
 * grow with the card. Clears whatever the graphics object drew before, so it can be called on every resize.
 */
export function drawPanel(graphics, left, top, width, height, s) {
    graphics.clear()
        .fillStyle(PANEL, 1)
        .fillRoundedRect(left, top, width, height, 28 * s)
        .lineStyle(3, 0xffffff, 1)
        .strokeRoundedRect(left, top, width, height, 28 * s);
}
