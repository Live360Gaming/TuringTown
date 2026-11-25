import {HOST_CENTER_X} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {textStyle} from '../../../../core/theme.js';
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
        this.add.text(HOST_CENTER_X, 140, final ? 'Final vote: who is the bot? This one decides it' : 'Who do you suspect? This fills the meter',
            {fontSize: '34px', color: '#ffffff'}).setOrigin(0.5);
        this.add.text(HOST_CENTER_X, 244, meterLine(data?.meter), textStyle('meta')).setOrigin(0.5);

        createTable(this);

        this.progressText = this.add.text(HOST_CENTER_X, 200, '', {fontSize: '32px', color: '#ffffff'}).setOrigin(0.5);
        this.listen(this.session, 'progress', () => this.showProgress());
        this.showProgress();
    }

    showProgress() {
        const {done, total} = this.session.progress;
        this.progressText.setText(total > 0 ? `${done} of ${total} voted` : '');
    }
}
