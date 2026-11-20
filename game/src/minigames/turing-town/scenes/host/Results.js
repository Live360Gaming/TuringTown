import {HOST_CENTER_X, HOST_HEIGHT, HOST_WIDTH} from '../../../../constants.js';
import {createAvatar} from '../../../../core/characters.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {createTextButton} from '../../../../core/ui/Button.js';

// The end of the game: who won, and who was a bot all along.
export class TuringTownHostResults extends HostStage {
    constructor() {
        super('TuringTownHostResults');
    }

    build() {
        const {winner, roles = []} = this.session.state.data ?? {};
        const humansWon = winner === 'Humans';
        this.setTitle(humansWon ? 'The humans win!' : 'The bots win!');

        this.add.text(HOST_CENTER_X, 120, humansWon ? 'Every bot was voted out.' : 'The bots blended in perfectly.', {fontSize: '32px', color: '#ffffff'}).setOrigin(0.5);

        const byId = new Map(this.session.roster.map(player => [player.id, player]));
        const gap = Math.min(170, (HOST_WIDTH - 120) / Math.max(roles.length, 1));
        const startX = HOST_CENTER_X - ((roles.length - 1) * gap) / 2;

        roles.forEach((role, i) => {
            const player = byId.get(role.playerId);
            if (!player) return;
            const x = startX + i * gap;
            createAvatar(this, x, 300, 100, player);
            this.add.text(x, 380, role.alias, {fontSize: '20px', color: player.color}).setOrigin(0.5);
            this.add.text(x, 415, role.wasBot ? 'BOT' : 'HUMAN', {fontSize: '26px', color: role.wasBot ? '#39ff14' : '#ffffff'}).setOrigin(0.5);
        });

        this.statusText = this.add.text(HOST_CENTER_X, 560, '', {fontSize: '24px', color: '#ff4d4d'}).setOrigin(0.5);
        createTextButton(this, HOST_CENTER_X, HOST_HEIGHT - 70, 'Back to lobby', () => this.backToLobby(), {width: 320, height: 64, fontSize: '30px'});
    }

    backToLobby() {
        this.session.invoke('ReturnToLobby').catch(err => this.statusText.setText(hubErrorMessage(err)));
    }
}
