import {HOST_CENTER_X, HOST_WIDTH} from '../../../../constants.js';
import {COLORS, HEX, textStyle} from '../../../../core/theme.js';

// The answer board on the TV: everyone's onboarding answers, one question at a time, in a strip under the header.
// It cycles through the questions so the room has something to argue about. Bots' answers look exactly like
// everyone else's: the server sends the board sorted by alias, with no roles.

const TOP = 96;
const HEIGHT = 128;
const LEFT = 60;
const WIDTH = HOST_WIDTH - 2 * LEFT;
const COLUMNS = 4;
const CELL_HEIGHT = 44;
const SECONDS_PER_QUESTION = 8;
const MAX_ANSWER_CHARS = 48;

/**
 * Draws the board from `session.state.data.board` and returns nothing. Does nothing when there is no board.
 * `scene` must be a HostStage.
 */
export function createAnswerBoard(scene) {
    const session = scene.session;
    const board = session.state?.data?.board ?? [];
    if (board.length === 0) return;

    const plate = scene.add.graphics().setDepth(2.5);
    plate.fillStyle(HEX.bgDeep, 0.78).fillRoundedRect(LEFT, TOP, WIDTH, HEIGHT, 16)
        .lineStyle(2, HEX.pink, 0.6).strokeRoundedRect(LEFT, TOP, WIDTH, HEIGHT, 16);

    const container = scene.add.container(0, 0).setDepth(2.6);
    let index = 0;

    const show = () => {
        container.removeAll(true);
        const question = board[index % board.length];
        const counter = board.length > 1 ? `  (${(index % board.length) + 1}/${board.length})` : '';

        container.add(scene.add.text(HOST_CENTER_X, TOP + 18, `${question.question}${counter}`, textStyle('meta', {
            fontSize: '18px', color: COLORS.pink, align: 'center',
            fixedWidth: WIDTH - 40,
        })).setOrigin(0.5, 0.5));

        const answers = question.answers ?? [];
        const rows = Math.max(1, Math.ceil(answers.length / COLUMNS));
        const cellWidth = WIDTH / Math.min(COLUMNS, Math.max(answers.length, 1));
        answers.forEach((entry, i) => {
            const row = Math.floor(i / COLUMNS);
            const inRow = Math.min(COLUMNS, answers.length - row * COLUMNS);
            const rowLeft = LEFT + (WIDTH - inRow * cellWidth) / 2;
            const x = rowLeft + (i % COLUMNS) * cellWidth + 14;
            const y = TOP + 40 + row * (rows > 1 ? CELL_HEIGHT : CELL_HEIGHT + 10);

            const player = session.roster.find(p => p.id === entry.playerId);
            const alive = player?.alive !== false;
            const answer = shorten(entry.answer) || '(no answer)';

            const name = scene.add.text(x, y, entry.alias, textStyle('name', {
                fontSize: '15px', color: player?.color ?? COLORS.cyan,
            })).setAlpha(alive ? 1 : 0.4);
            const text = scene.add.text(x, y + 18, answer, textStyle('body', {
                fontSize: '16px', color: entry.answer ? COLORS.text : COLORS.textDim,
                fixedWidth: cellWidth - 24, wordWrap: {width: cellWidth - 24}, maxLines: 1,
            })).setAlpha(alive ? 1 : 0.4);
            container.add([name, text]);
        });

        container.setAlpha(0);
        scene.tweens.add({targets: container, alpha: 1, duration: 300});
    };

    show();
    if (board.length > 1) {
        scene.time.addEvent({
            delay: SECONDS_PER_QUESTION * 1000, loop: true,
            callback: () => {
                index++;
                show();
            },
        });
    }

    // Someone voted out is dimmed as soon as the roster says so.
    scene.listen(session, 'roster', show);
}

function shorten(text) {
    const clean = (text ?? '').trim();
    return clean.length > MAX_ANSWER_CHARS ? `${clean.slice(0, MAX_ANSWER_CHARS - 1).trimEnd()}...` : clean;
}
