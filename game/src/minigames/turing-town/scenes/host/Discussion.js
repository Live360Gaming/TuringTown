import {HostStage} from '../../../../core/stage/HostStage.js';
import {createTable} from './table.js';

// The anonymous chat on the TV: everyone around a table in a random order, each message in a bubble above
// whoever said it. Players only ever appear as their alias and character. Form answers stay private, so none are shown.
export class TuringTownHostDiscussion extends HostStage {
    constructor() {
        super('TuringTownHostDiscussion');
    }

    build() {
        this.setTitle(`Round ${this.session.state.round}: Discuss`);

        const table = createTable(this, {drift: true});
        table.showRecent(this.session.transcript);

        // A soft pop on the TV for every new line.
        this.listen(this.session, 'chat', () => this.sound.play('message_sfx2'));
    }
}
