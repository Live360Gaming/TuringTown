import {HostStage} from '../../../../core/stage/HostStage.js';
import {createTable} from './table.js';
import {createAnswerBoard} from './answerBoard.js';

// The anonymous chat on the TV: everyone around a table in a random order, each message in a bubble above
// whoever said it, under the answer board. Players only ever appear as their alias and character.
export class TuringTownHostDiscussion extends HostStage {
    constructor() {
        super('TuringTownHostDiscussion');
    }

    build() {
        this.setTitle(`Round ${this.session.state.round}: Discuss`);

        const table = createTable(this, {drift: true});
        table.showRecent(this.session.transcript);

        // Everyone's form answers, one question at a time, for the room to argue about.
        createAnswerBoard(this);

        // A soft pop on the TV for every new line.
        this.listen(this.session, 'chat', () => this.sound.play('message_sfx2'));
    }
}
