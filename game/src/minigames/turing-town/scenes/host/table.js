import * as Phaser from 'phaser';
import {HOST_CENTER_X, HOST_WIDTH} from '../../../../constants.js';
import {createAvatar} from '../../../../core/characters.js';

// The TV's game screen: everyone sits in a random order along a long table, and what they say pops up in a
// speech bubble above their character. Used by the discussion and the vote.

// Layout on the 1280x720 TV. The table (assets/table.png, cropped to its edges) is scaled to TABLE_WIDTH and sits
// on the bottom of the screen. Its polished counter top starts about 63px below its top edge and the front lip is
// about 114px down, so a character centred at AVATAR_Y is hidden below the counter's back edge, and NAME_Y is the
// middle of the counter top.
const TABLE_WIDTH = 1200;
const TABLE_BOTTOM = 712;
const AVATAR_Y = 410;
const NAME_Y = 490;
const BUBBLE_WIDTH = 230;
const BUBBLE_SECONDS = 7;

/**
 * Draws the table and the seated characters (redrawn as the roster changes) and returns
 * `{showBubble(message), showRecent(transcript)}`. `scene` must be a HostStage.
 *
 * With `drift: true` the living characters slowly shuffle a little left and right at random, like people
 * talking round a table. They never stray far from their seat, so neighbours do not pile up.
 */
export function createTable(scene, {drift = false} = {}) {
    const session = scene.session;
    const seats = new Map(); // player id -> {x, size, index, avatar}
    const bubbles = new Map(); // player id -> container
    let items = [];
    let driftTweens = [];

    // Each character keeps wandering to a new random spot near its seat, at its own pace.
    const startDrift = (avatar, seatX, range) => {
        const step = () => {
            if (!avatar.active) return;
            const tween = scene.tweens.add({
                targets: avatar,
                x: seatX + Phaser.Math.Between(-range, range),
                duration: Phaser.Math.Between(1200, 3200),
                delay: Phaser.Math.Between(0, 600),
                ease: 'Sine.easeInOut',
                onComplete: step,
            });
            driftTweens.push(tween);
        };
        step();
    };

    const draw = () => {
        driftTweens.forEach(tween => tween.remove());
        driftTweens = [];
        items.forEach(item => item.destroy());
        items = [];
        seats.clear();

        const players = session.seatedRoster();
        const gap = (HOST_WIDTH - 220) / Math.max(players.length, 1);
        const size = Math.min(150, gap * 0.95);
        const startX = HOST_CENTER_X - ((players.length - 1) * gap) / 2;

        // Behind the table: the characters. In front of it: the table, then the name cards on top.
        players.forEach((player, i) => {
            const x = startX + i * gap;
            const avatar = createAvatar(scene, x, AVATAR_Y, size, player, {dim: !player.alive}).setDepth(0);
            seats.set(player.id, {x, size, index: i, avatar});
            items.push(avatar);
            if (drift && player.alive) startDrift(avatar, x, gap * 0.15);
        });

        // The table image is cropped to its edges, so it can be placed by its bottom centre.
        const table = scene.add.image(HOST_CENTER_X, TABLE_BOTTOM, 'table').setOrigin(0.5, 1).setDepth(1);
        table.setScale(TABLE_WIDTH / table.width);
        items.push(table);

        // The name cards sit on the polished counter top, clear of the sign on the front of the table.
        players.forEach(player => {
            const {x} = seats.get(player.id);
            items.push(scene.add.text(x, NAME_Y, player.alias, {
                fontSize: '22px', fontStyle: 'bold', color: player.color, stroke: '#000000', strokeThickness: 4,
            }).setOrigin(0.5).setDepth(2).setAlpha(player.alive ? 1 : 0.4));
        });
    };

    scene.listen(session, 'roster', draw);
    draw();

    // A bubble above the speaker. Neighbours alternate between two heights so wide bubbles do not sit on each other.
    const showBubble = message => {
        const seat = seats.get(message.playerId);
        if (!seat) return;

        bubbles.get(message.playerId)?.destroy();

        const text = scene.add.text(0, 0, message.text, {
            fontSize: '22px', color: '#1a1a1a', align: 'center', wordWrap: {width: BUBBLE_WIDTH - 24},
        }).setOrigin(0.5, 1);
        const height = text.height + 24;

        const background = scene.add.graphics();
        // A message that @mentions someone gets a golden bubble, so the room sees who is being spoken to.
        const bubbleColor = message.mentions?.length ? 0xffe082 : 0xffffff;
        background.fillStyle(bubbleColor, 1).fillRoundedRect(-BUBBLE_WIDTH / 2, -height - 10, BUBBLE_WIDTH, height, 18);
        background.fillTriangle(-12, -10, 12, -10, 0, 6);
        text.setPosition(0, -22);

        const stagger = seat.index % 2 === 0 ? 0 : 90;
        // Above wherever the character is standing right now (they drift during the discussion).
        const x = Math.min(HOST_WIDTH - BUBBLE_WIDTH / 2 - 10, Math.max(BUBBLE_WIDTH / 2 + 10, seat.avatar.x));
        const bubble = scene.add.container(x, AVATAR_Y - seat.size / 2 - 14 - stagger, [background, text]).setDepth(3);
        bubbles.set(message.playerId, bubble);

        scene.time.delayedCall(BUBBLE_SECONDS * 1000, () => {
            if (bubbles.get(message.playerId) !== bubble) return;
            scene.tweens.add({
                targets: bubble, alpha: 0, duration: 400,
                onComplete: () => {
                    bubble.destroy();
                    if (bubbles.get(message.playerId) === bubble) bubbles.delete(message.playerId);
                },
            });
        });
    };

    scene.listen(session, 'chat', showBubble);

    // The TV may open mid-discussion (a refresh): show what was said in the last few seconds.
    const showRecent = transcript => {
        const cutoff = session.serverNow() - BUBBLE_SECONDS * 1000;
        const latest = new Map();
        transcript.forEach(message => latest.set(message.playerId, message));
        latest.forEach(message => {
            if (message.sentAtUtcMs >= cutoff) showBubble(message);
        });
    };

    return {showBubble, showRecent};
}
