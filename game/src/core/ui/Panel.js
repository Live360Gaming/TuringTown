import {PANEL} from './Button.js';
import {HEX} from '../theme.js';

/**
 * Draws the rounded card the player screens sit on. `s` is the window's scale factor, so the corner and border
 * grow with the card. Clears whatever the graphics object drew before, so it can be called on every resize.
 */
export function drawPanel(graphics, left, top, width, height, s, {headerH = 0} = {}) {
    const r = Math.max(16, 24 * s);
    graphics.clear()
        // A faint glow, then the solid card, then a thin neon edge. No blur, so text stays crisp.
        .lineStyle(10, HEX.cyan, 0.08).strokeRoundedRect(left, top, width, height, r)
        .fillStyle(PANEL, 0.96)
        .fillRoundedRect(left, top, width, height, r)
        .lineStyle(2, HEX.cyan, 0.45)
        .strokeRoundedRect(left, top, width, height, r);

    if (headerH > 0) {
        // The terminal header: a darker strip with faint scanlines and a divider.
        graphics.fillStyle(HEX.bgDeep, 0.6).fillRoundedRect(left + 1, top + 1, width - 2, headerH, {tl: r, tr: r, bl: 0, br: 0});
        graphics.lineStyle(1, HEX.cyan, 0.05);
        for (let y = top + 4; y < top + headerH; y += 4) graphics.lineBetween(left + r / 2, y, left + width - r / 2, y);
        graphics.lineStyle(1, HEX.cyan, 0.3).lineBetween(left + 1, top + headerH, left + width - 1, top + headerH);
    }
}
