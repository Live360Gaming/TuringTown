import {HOST_CENTER_X, HOST_CENTER_Y} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';

// Everyone fills in the onboarding form on their phone. Answers are private, so the TV only shows progress.
export class TuringTownHostOnboarding extends HostStage {
    constructor() {
        super('TuringTownHostOnboarding');
    }

    build() {
        this.setTitle('Get to know each other');
        this.add.text(HOST_CENTER_X, HOST_CENTER_Y - 70, 'Answer the questions on your phone', {fontSize: '40px', color: '#ffffff'}).setOrigin(0.5);

        this.progressText = this.add.text(HOST_CENTER_X, HOST_CENTER_Y - 10, '', {fontSize: '32px', color: '#ffffff'}).setOrigin(0.5);
        this.listen(this.session, 'progress', () => this.showProgress());
        this.showProgress();

        this.createRosterRow(HOST_CENTER_Y + 140);
    }

    showProgress() {
        const {done, total} = this.session.progress;
        this.progressText.setText(total > 0 ? `${done} of ${total} done` : '');
    }
}
