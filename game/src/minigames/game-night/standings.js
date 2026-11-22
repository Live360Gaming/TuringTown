import {createAvatar} from '../../core/characters.js';
import {COLORS, HEX, textStyle} from '../../core/theme.js';
import {addNameplate} from '../../core/ui/Nameplate.js';

/**
 * A ranked list of players and their points on the TV: rank, avatar, name tag and points on a dark plate per row.
 * `standings` is [{playerId, alias, points}] in order. Players not in the roster any more are skipped.
 * Returns the objects it made.
 */
export function drawStandings(scene, standings, {x, y, width, rowH = 58, max = 8}) {
    const byId = new Map(scene.session.roster.map(player => [player.id, player]));
    const items = [];

    standings.slice(0, max).forEach((standing, i) => {
        const player = byId.get(standing.playerId) ?? {id: standing.playerId, alias: standing.alias, color: COLORS.border, characterId: 'character_01'};
        const top = y + i * rowH;
        const cy = top + (rowH - 8) / 2;

        const plate = scene.add.graphics()
            .fillStyle(HEX.bgDeep, i === 0 ? 0.92 : 0.8).fillRoundedRect(x, top, width, rowH - 8, 14)
            .lineStyle(2, i === 0 ? HEX.gold : HEX.border, 0.9).strokeRoundedRect(x, top, width, rowH - 8, 14);
        const rank = scene.add.text(x + 30, cy, `${i + 1}`, textStyle('title', {fontSize: '26px', color: i === 0 ? COLORS.gold : COLORS.text, strokeThickness: 4})).setOrigin(0.5);
        const avatar = createAvatar(scene, x + 80, cy, rowH - 14, player);
        const name = addNameplate(scene, x + 120, cy, player, {maxWidth: width - 260, fontSize: 20});
        name.setX(x + 120 + name.width / 2);
        const points = scene.add.text(x + width - 24, cy, `${standing.points}`, textStyle('meta', {fontSize: '26px', color: COLORS.gold})).setOrigin(1, 0.5);
        items.push(plate, rank, avatar, name, points);
    });

    return items;
}

/** "Humans 1200 vs AI 900" as two big plates side by side, centred on `cx`. */
export function drawTeamTotals(scene, teams, {cx, y, width = 520}) {
    const half = width / 2 - 10;
    const items = [];
    [['HUMANS', teams.humans, HEX.cyan, COLORS.cyan, cx - half / 2 - 10], ['AI', teams.ai, HEX.green, COLORS.green, cx + half / 2 + 10]]
        .forEach(([label, points, hex, css, x]) => {
            items.push(
                scene.add.graphics().fillStyle(HEX.bgDeep, 0.9).fillRoundedRect(x - half / 2, y, half, 120, 18)
                    .lineStyle(3, hex, 1).strokeRoundedRect(x - half / 2, y, half, 120, 18),
                scene.add.text(x, y + 32, label, textStyle('meta', {fontSize: '24px', color: css})).setOrigin(0.5),
                scene.add.text(x, y + 80, `${points}`, textStyle('title', {fontSize: '46px'})).setOrigin(0.5),
            );
        });
    return items;
}
