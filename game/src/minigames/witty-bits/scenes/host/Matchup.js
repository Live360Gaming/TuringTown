import {HostStage} from '../../../../core/stage/HostStage.js';
import {HEX} from '../../../../core/theme.js';
import {cardSlots, drawAnswerCard, drawPrompt} from './cards.js';

// One prompt and its answers, with no names. Everyone who did not write them votes on their phone.
export class WittyBitsHostMatchup extends HostStage {
    constructor() {
        super('WittyBitsHostMatchup');
    }

    build() {
        const {round, lastBit, index, count, prompt, answers = []} = this.session.state.data ?? {};
        this.setTitle(lastBit ? 'The Last Bit' : `Round ${round}: ${index + 1} of ${count}`);
        this.setInstruction(lastBit ? 'Vote for your favorite (not your own!)' : 'Vote for the funnier answer on your phone');

        drawPrompt(this, prompt ?? '');
        const slots = cardSlots(answers.length, answers.length <= 2 ? 300 : 270);
        const edges = [HEX.cyan, HEX.pink, HEX.gold, HEX.green];
        answers.forEach((answer, i) => drawAnswerCard(this, slots[i], answer, {edge: edges[i % edges.length]}));

        this.listen(this.session, 'progress', () => this.showProgress());
        this.showProgress();
    }

    showProgress() {
        const {done, total} = this.session.progress;
        this.setProgress(done, total);
    }
}
