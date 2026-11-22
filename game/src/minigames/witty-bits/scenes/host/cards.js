import {HOST_CENTER_X, HOST_WIDTH} from '../../../../constants.js';
import {COLORS, HEX, textStyle} from '../../../../core/theme.js';

/** The prompt on a gold-edged plate under the header. Returns the objects. */
export function drawPrompt(scene, text, y = 190) {
    const label = scene.add.text(HOST_CENTER_X, y, text, textStyle('title', {fontSize: '34px', align: 'center', wordWrap: {width: HOST_WIDTH - 260}, strokeThickness: 5}))
        .setOrigin(0.5);
    const h = Math.max(90, label.height + 40);
    const plate = scene.add.graphics()
        .fillStyle(HEX.bgDeep, 0.9).fillRoundedRect(100, y - h / 2, HOST_WIDTH - 200, h, 22)
        .lineStyle(3, HEX.gold, 1).strokeRoundedRect(100, y - h / 2, HOST_WIDTH - 200, h, 22);
    label.setDepth(1);
    return [plate, label];
}

/**
 * Where each answer card goes: two big cards side by side for a matchup, or a grid for the Last Bit.
 * Returns [{x, y, w, h}] for `count` cards whose tops start at `top`.
 */
export function cardSlots(count, top) {
    if (count <= 2) {
        const w = 500, h = 220, gap = 60;
        const start = HOST_CENTER_X - ((count - 1) * (w + gap)) / 2;
        return Array.from({length: count}, (_, i) => ({x: start + i * (w + gap), y: top + h / 2, w, h}));
    }

    const cols = count <= 4 ? 2 : count <= 6 ? 3 : 4;
    const w = cols === 2 ? 480 : cols === 3 ? 360 : 280;
    const h = count <= 6 ? 130 : 110;
    // Room under each card for its author and points once the vote is over.
    const gapX = 24, gapY = 50;
    const rows = Math.ceil(count / cols);
    return Array.from({length: count}, (_, i) => {
        const row = Math.floor(i / cols);
        const inRow = row === rows - 1 ? count - row * cols : cols;
        const col = i % cols;
        const start = HOST_CENTER_X - ((inRow - 1) * (w + gapX)) / 2;
        return {x: start + col * (w + gapX), y: top + h / 2 + row * (h + gapY), w, h};
    });
}

/**
 * One answer on a card: the letter, the text (dimmed when nobody answered), and an optional colored edge.
 * Returns a container centred on the slot.
 */
export function drawAnswerCard(scene, {x, y, w, h}, answer, {edge = HEX.cyan, highlight = false} = {}) {
    const bg = scene.add.graphics()
        .fillStyle(highlight ? HEX.surfaceRaised : HEX.bgDeep, 0.92).fillRoundedRect(-w / 2, -h / 2, w, h, 18)
        .lineStyle(highlight ? 5 : 3, edge, 1).strokeRoundedRect(-w / 2, -h / 2, w, h, 18);
    const letter = scene.add.text(-w / 2 + 22, -h / 2 + 20, answer.id.toUpperCase(), textStyle('meta', {fontSize: '22px', color: COLORS.gold})).setOrigin(0, 0.5);
    const size = h >= 200 ? 30 : 22;
    const text = scene.add.text(0, 6, answer.text, textStyle(answer.missing ? 'body' : 'name', {
        fontSize: `${size}px`, align: 'center', wordWrap: {width: w - 50}, color: answer.missing ? COLORS.textDim : COLORS.text,
    })).setOrigin(0.5);
    return scene.add.container(x, y, [bg, letter, text]).setSize(w, h);
}
