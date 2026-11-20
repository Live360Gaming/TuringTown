import {HOST_CENTER_X} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {createTable} from './table.js';

// Players vote on their phones. The TV keeps the same table and seats, and shows how many votes are in,
// not who voted for whom.
export class TuringTownHostVoting extends HostStage {
    constructor() {
        super('TuringTownHostVoting');
    }

    build() {
        this.setTitle(`Round ${this.session.state.round}: Vote someone out`);
        this.add.text(HOST_CENTER_X, 140, 'Who is the bot? Vote on your phone', {fontSize: '34px', color: '#ffffff'}).setOrigin(0.5);

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
