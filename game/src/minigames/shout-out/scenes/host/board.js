import {HOST_CENTER_X, HOST_WIDTH} from '../../../../constants.js';
import {COLORS, HEX, textStyle} from '../../../../core/theme.js';

const EDGES = [HEX.cyan, HEX.pink, HEX.gold, HEX.green];

/** "Question 3 of 10", the category, and "DOUBLE POINTS" when it is. */
export function questionTitle({index = 0, count = 0, category = '', double: doubled = false}) {
    return `${doubled ? 'DOUBLE POINTS · ' : ''}Question ${index + 1} of ${count} · ${category}`;
}

/** The question on a gold-edged plate. Returns its bottom edge. */
export function drawQuestion(scene, text, y = 200) {
    const label = scene.add.text(HOST_CENTER_X, y, text, textStyle('title', {
        fontSize: '34px', align: 'center', wordWrap: {width: HOST_WIDTH - 280}, strokeThickness: 5,
    })).setOrigin(0.5).setDepth(1);
    const h = Math.max(100, label.height + 44);
    scene.add.graphics()
        .fillStyle(HEX.bgDeep, 0.9).fillRoundedRect(110, y - h / 2, HOST_WIDTH - 220, h, 22)
        .lineStyle(3, HEX.gold, 1).strokeRoundedRect(110, y - h / 2, HOST_WIDTH - 220, h, 22);
    return y + h / 2;
}

/**
 * The four answers in a 2x2 grid under `top`. `mark(option)` may return 'right', 'wrong' or 'dim' to color a card.
 * Returns the cards (containers), in option order.
 */
export function drawOptions(scene, options, top, mark = () => null) {
    const w = 500, h = 92, gapX = 30, gapY = 22;
    return options.map((option, i) => {
        const x = HOST_CENTER_X + (i % 2 === 0 ? -1 : 1) * (w + gapX) / 2;
        const y = top + h / 2 + Math.floor(i / 2) * (h + gapY);
        const state = mark(option);
        const edge = state === 'right' ? HEX.green : state === 'wrong' ? HEX.danger : EDGES[i % EDGES.length];
        const bg = scene.add.graphics()
            .fillStyle(state === 'right' ? HEX.surfaceRaised : HEX.bgDeep, 0.92).fillRoundedRect(-w / 2, -h / 2, w, h, 18)
            .lineStyle(state ? 5 : 3, edge, 1).strokeRoundedRect(-w / 2, -h / 2, w, h, 18);
        const letter = scene.add.text(-w / 2 + 26, 0, option.id.toUpperCase(), textStyle('title', {fontSize: '30px', color: COLORS.gold}))
            .setOrigin(0.5);
        const text = scene.add.text(14, 0, option.text, textStyle('name', {
            fontSize: '28px', align: 'center', wordWrap: {width: w - 90},
            color: state === 'dim' ? COLORS.textDim : COLORS.text,
        })).setOrigin(0.5);
        return scene.add.container(x, y, [bg, letter, text]).setAlpha(state === 'dim' ? 0.55 : 1);
    });
}
