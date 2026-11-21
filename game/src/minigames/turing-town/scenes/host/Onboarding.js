import {HOST_CENTER_Y} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';

// Everyone fills in the onboarding form on their phone. Answers are private, so the TV only shows progress.
export class TuringTownHostOnboarding extends HostStage {
    constructor() {
        super('TuringTownHostOnboarding');
    }

    build() {
        this.setTitle('Get to know each other');
        this.setInstruction('Answer the questions on your phone');
        this.listen(this.session, 'progress', () => this.showProgress());
        this.showProgress();

        this.createRosterRow(HOST_CENTER_Y + 190);
    }

    showProgress() {
        const {done, total} = this.session.progress;
        this.setProgress(done, total);
    }
}
