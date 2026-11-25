import {HOST_CENTER_X} from '../../../../constants.js';
import {HEX, textStyle} from '../../../../core/theme.js';

const BAR_W = 520;

/**
 * The suspicion meter on the TV: one bar per player, most suspected first. `meter` comes from the server as
 * [{playerId, alias, votes, percent}] and is built only from votes. The bars grow from zero for a little drama.
 */
export function addMeter(scene, meter, {y, rowHeight = 44} = {}) {
    meter.forEach((level, i) => {
        const rowY = y + i * rowHeight;
        const left = HOST_CENTER_X - BAR_W / 2;
        scene.add.text(left - 16, rowY, level.alias, textStyle('name', {fontSize: '24px'})).setOrigin(1, 0.5);
        scene.add.rectangle(left, rowY, BAR_W, 24, HEX.surfaceRaised).setOrigin(0, 0.5);
        const fill = scene.add.rectangle(left, rowY, BAR_W * level.percent / 100, 24, i === 0 && level.votes > 0 ? HEX.pink : HEX.cyan)
            .setOrigin(0, 0.5).setScale(0, 1);
        scene.tweens.add({targets: fill, scaleX: 1, duration: 900, delay: i * 120, ease: 'Cubic.easeOut'});
        scene.add.text(left + BAR_W + 16, rowY, `${level.percent}%`, textStyle('name', {fontSize: '24px'})).setOrigin(0, 0.5);
    });
}
