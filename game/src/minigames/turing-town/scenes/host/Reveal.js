import {HOST_CENTER_X} from '../../../../constants.js';
import {createAvatar} from '../../../../core/characters.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {ENDINGS, sayLine} from '../../data/transitions.js';
import {addMeter} from './meter.js';

// The result of the vote. This is the first time anyone learns whether a player was a bot.
export class TuringTownHostReveal extends HostStage {
    constructor() {
        super('TuringTownHostReveal');
    }

    build() {
        const {eliminated, wasBot, tally = [], final, meter = []} = this.session.state.data ?? {};
        this.setTitle(final ? 'The verdict' : 'Suspicion meter');

        // Before the final vote nobody leaves: the votes only move the meter.
        if (!final) {
            addMeter(this, meter, {y: 200, rowHeight: Math.min(60, 420 / Math.max(meter.length, 1))});
            return;
        }

        const ending = !eliminated ? ENDINGS.no_verdict : wasBot ? ENDINGS.humans_win : ENDINGS.bots_win;
        this.setInstruction(ending.caption);
        sayLine(this, Object.keys(ENDINGS).find(id => ENDINGS[id] === ending));

        if (eliminated) {
            createAvatar(this, HOST_CENTER_X, 230, 190, eliminated);
            this.add.text(HOST_CENTER_X, 370, eliminated.alias, {fontSize: '44px', color: eliminated.color}).setOrigin(0.5);
            this.add.text(HOST_CENTER_X, 425, wasBot ? 'was an AI BOT!' : 'was a human...', {
                fontSize: '40px', color: wasBot ? '#39ff14' : '#ff4d4d',
            }).setOrigin(0.5);
        } else {
            this.add.text(HOST_CENTER_X, 260, 'No verdict!\nThe bots slip away.', {fontSize: '48px', color: '#ffffff', align: 'center'}).setOrigin(0.5);
        }

        const lines = tally.map(t => `${t.alias}: ${t.votes} ${t.votes === 1 ? 'vote' : 'votes'}`).join('    ');
        this.add.text(HOST_CENTER_X, 560, lines || 'Nobody voted.', {fontSize: '26px', color: '#ffffff', align: 'center', wordWrap: {width: 1100}}).setOrigin(0.5);
    }
}
