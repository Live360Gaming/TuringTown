import {createAvatar} from '../../../../core/characters.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {COLORS, HEX, textStyle} from '../../../../core/theme.js';
import {addNameplate} from '../../../../core/ui/Nameplate.js';
import {cardSlots, drawAnswerCard, drawPrompt} from './cards.js';

// Who wrote what, the votes and the points. The answer with the most votes is highlighted.
export class WittyBitsHostMatchupResult extends HostStage {
    constructor() {
        super('WittyBitsHostMatchupResult');
    }

    build() {
        const {round, lastBit, index, count, prompt, answers = []} = this.session.state.data ?? {};
        this.setTitle(lastBit ? 'The Last Bit' : `Round ${round}: ${index + 1} of ${count}`);
        this.timerText.setVisible(false);

        drawPrompt(this, prompt ?? '');
        const best = Math.max(...answers.map(a => a.votes), 0);
        const byId = new Map(this.session.roster.map(player => [player.id, player]));
        const big = answers.length <= 2;
        const slots = cardSlots(answers.length, big ? 280 : 260);

        answers.forEach((answer, i) => {
            const slot = slots[i];
            const top = answer.votes === best && best > 0;
            drawAnswerCard(this, {...slot, h: slot.h}, answer, {edge: top ? HEX.gold : HEX.border, highlight: top});

            // Under the card: the author, and what the answer earned.
            const player = byId.get(answer.authorId) ?? {id: answer.authorId, alias: answer.alias, color: COLORS.border, characterId: 'character_01'};
            const below = slot.y + slot.h / 2 + (big ? 50 : 30);
            const avatarSize = big ? 64 : 40;
            createAvatar(this, slot.x - slot.w / 2 + avatarSize / 2 + 10, below, avatarSize, player);
            const plate = addNameplate(this, 0, below, player, {maxWidth: slot.w - avatarSize - 140, fontSize: big ? 20 : 16});
            plate.setX(slot.x - slot.w / 2 + avatarSize + 20 + plate.width / 2);

            const votes = `${answer.votes} vote${answer.votes === 1 ? '' : 's'}`;
            this.add.text(slot.x + slot.w / 2 - 10, below - (big ? 14 : 0), big ? votes : `+${answer.points}`, textStyle('meta', {fontSize: big ? '22px' : '20px', color: COLORS.text}))
                .setOrigin(1, 0.5);
            if (big) {
                this.add.text(slot.x + slot.w / 2 - 10, below + 16, `+${answer.points}`, textStyle('meta', {fontSize: '26px', color: COLORS.gold})).setOrigin(1, 0.5);
            }
            if (answer.sweep) {
                this.add.text(slot.x, slot.y - slot.h / 2 - 4, 'CLEAN SWEEP!', textStyle('title', {fontSize: '26px', color: COLORS.gold})).setOrigin(0.5);
            }
        });
    }
}
