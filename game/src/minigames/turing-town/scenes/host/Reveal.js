import {HOST_CENTER_X} from '../../../../constants.js';
import {createAvatar} from '../../../../core/characters.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {getNarration} from '../../../../core/narration/index.js';
import {endingKey} from '../../../../core/narration/scopes.js';
import {narrationEntry} from '../../../../narration/registry.js';
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

        // Exactly one ending line, chosen from the server's result; its words show under the verdict.
        const ending = endingKey(this.session.state.data);
        this.add.text(HOST_CENTER_X, 500, narrationEntry(ending).transcript, {fontSize: '28px', color: '#ffd476', align: 'center', wordWrap: {width: 1100}}).setOrigin(0.5);
        getNarration(this.game).enqueue([ending], {occurrence: 'ending'});

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
