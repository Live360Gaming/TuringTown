import {HOST_CENTER_X, HOST_HEIGHT, HOST_WIDTH} from '../../../../constants.js';
import {createAvatar} from '../../../../core/characters.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {COLORS, textStyle} from '../../../../core/theme.js';
import {addNameplate} from '../../../../core/ui/Nameplate.js';
import {addContinueButton} from '../../continue.js';
import {getNarration} from '../../../../core/narration/index.js';
import {goodbyeKeys, goodbyeText} from '../../quips.js';
import {drawTeamTotals} from '../../standings.js';

const HEADLINES = {
    Humans: 'The humans win the night!',
    AI: 'The AI wins the night!',
    Tie: 'A perfect tie!',
};

// The end of the night: the Humans vs AI totals, who won, and who was an AI all along.
export class GameNightHostFinale extends HostStage {
    constructor() {
        super('GameNightHostFinale');
    }

    build() {
        const {winner, teams = {humans: 0, ai: 0}, roles = []} = this.session.state.data ?? {};
        this.setTitle(HEADLINES[winner] ?? 'Game over');

        drawTeamTotals(this, teams, {cx: HOST_CENTER_X, y: 120, width: 600});

        const byId = new Map(this.session.roster.map(player => [player.id, player]));
        const gap = Math.min(170, (HOST_WIDTH - 120) / Math.max(roles.length, 1));
        const startX = HOST_CENTER_X - ((roles.length - 1) * gap) / 2;
        roles.forEach((role, i) => {
            const player = byId.get(role.playerId);
            if (!player) return;
            const x = startX + i * gap;
            createAvatar(this, x, 380, 110, player);
            addNameplate(this, x, 460, player, {maxWidth: gap - 8, fontSize: 18});
            this.add.text(x, 500, role.wasBot ? 'AI' : role.imposter ? 'IMPOSTER' : 'HUMAN',
                textStyle('meta', {fontSize: '24px', color: role.wasBot ? COLORS.green : role.imposter ? COLORS.gold : COLORS.text})).setOrigin(0.5);
        });

        const {goodbye} = this.session.state.data ?? {};
        this.add.text(HOST_CENTER_X, 560, goodbyeText(goodbye), textStyle('instruction', {fontSize: '28px', color: COLORS.gold, align: 'center'}))
            .setOrigin(0.5);
        getNarration(this.game).enqueue(goodbyeKeys(goodbye), {occurrence: 'finale'});

        addContinueButton(this, HOST_CENTER_X, HOST_HEIGHT - 80, 'Back to lobby', {width: 340});
    }
}
