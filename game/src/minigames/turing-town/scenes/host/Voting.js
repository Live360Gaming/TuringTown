import {HOST_CENTER_X, HOST_WIDTH} from '../../../../constants.js';
import {createAvatar} from '../../../../core/characters.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {HEX, textStyle} from '../../../../core/theme.js';
import {meterLine, roundTitle} from '../../rounds.js';
import {createTable} from './table.js';

// Players vote on their phones. The TV keeps the same table and seats, and shows how many votes are in,
// not who voted for whom.
export class TuringTownHostVoting extends HostStage {
    constructor() {
        super('TuringTownHostVoting');
    }

    build() {
        const {data} = this.session.state;
        const final = data?.kind === 'Final';
        this.setTitle(roundTitle(this.session.state));
        // The vote on the five questions: everyone's answers side by side.
        if (data?.kind === 'RapidFire') {
            this.setInstruction('Who seems off? Tap them on your phone. Nobody leaves yet.');
            this.showAnswers(data);
            this.progressText = this.add.text(HOST_CENTER_X, 690, '', textStyle('meta', {fontSize: '22px'})).setOrigin(0.5);
            this.listen(this.session, 'progress', () => this.showProgress());
            this.showProgress();
            this.speak('tap', data.hostLine);
            return;
        }

        this.setInstruction(final ? 'Final vote: who is the bot? This one decides it.' : 'Who do you suspect? This fills the meter.');
        this.add.text(HOST_CENTER_X, 156, meterLine(data?.meter), textStyle('meta')).setOrigin(0.5);

        createTable(this);

        this.progressText = this.add.text(HOST_CENTER_X, 200, '', textStyle('instruction', {fontSize: '30px'})).setOrigin(0.5);
        this.listen(this.session, 'progress', () => this.showProgress());
        this.showProgress();
        if (final) this.speak('final', data.hostLine);
    }

    showAnswers({questions = [], answers = []}) {
        const questionList = (this.session.state.data?.questions ?? questions);
        const column = (HOST_WIDTH - 60) / Math.max(answers.length, 1);
        answers.forEach((entry, i) => {
            const x = 30 + column * (i + 0.5);
            const player = this.session.roster.find(p => p.id === entry.playerId);
            // A dark card behind each player's column, so the answers read against the diner.
            this.add.graphics().fillStyle(HEX.bgDeep, 0.88).fillRoundedRect(x - column / 2 + 6, 145, column - 12, 520, 16);
            if (player) createAvatar(this, x, 195, 80, player);
            this.add.text(x, 250, entry.alias, textStyle('name', {fontSize: '20px', color: player?.color})).setOrigin(0.5);
            let y = 280;
            entry.answers.forEach((answer, n) => {
                const label = questionList[n] ? `${n + 1}. ` : '';
                const text = this.add.text(x, y, `${label}${answer ?? '...'}`, textStyle('instruction', {
                    fontSize: '20px', align: 'center', wordWrap: {width: column - 28},
                })).setOrigin(0.5, 0);
                y += Math.max(text.height + 12, 76);
            });
        });
    }

    showProgress() {
        const {done, total} = this.session.progress;
        this.progressText.setText(total > 0 ? `${done} of ${total} voted` : '');
    }
}
