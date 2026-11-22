import {HOST_CENTER_X, HOST_HEIGHT, HOST_WIDTH} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {COLORS, textStyle} from '../../../../core/theme.js';
import {addContinueButton} from '../../continue.js';
import {drawStandings, drawTeamTotals} from '../../standings.js';

// Between two games: everyone's points so far, the team totals once no bot is hiding any more, and what is next.
export class GameNightHostScoreboard extends HostStage {
    constructor() {
        super('GameNightHostScoreboard');
    }

    build() {
        const {next, standings = [], teams} = this.session.state.data ?? {};
        this.setTitle('Scoreboard');
        this.setInstruction(next ? `Up next: ${next.title}` : 'Up next: the finale');

        drawStandings(this, standings, {x: 60, y: 150, width: 560, max: 8});

        const rightX = HOST_WIDTH - 340;
        if (teams) {
            this.add.text(rightX, 170, 'Humans vs AI', textStyle('instruction')).setOrigin(0.5);
            drawTeamTotals(this, teams, {cx: rightX, y: 200, width: 520});
        } else {
            // A team total could point at the one player whose points it matches, so it stays hidden while a bot hides.
            this.add.text(rightX, 260, 'The team totals stay secret\nwhile an AI is still hiding.', textStyle('bodyOnArt', {fontSize: '26px', align: 'center'})).setOrigin(0.5);
            this.add.text(rightX, 340, 'Who is human? Keep guessing.', textStyle('body', {fontSize: '22px', color: COLORS.gold})).setOrigin(0.5);
        }

        addContinueButton(this, HOST_CENTER_X + 300, HOST_HEIGHT - 80, next ? `Play ${next.title}` : 'Continue', {width: 360});
    }
}
