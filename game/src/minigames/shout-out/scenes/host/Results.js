import {HOST_CENTER_X, HOST_HEIGHT} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {addContinueButton} from '../../../game-night/continue.js';
import {drawStandings} from '../../../game-night/standings.js';

// The end of Shout Out: everyone's points this game. Who is an AI is not said here: the night keeps that secret.
export class ShoutOutHostResults extends HostStage {
    constructor() {
        super('ShoutOutHostResults');
    }

    build() {
        const {standings = []} = this.session.state.data ?? {};
        this.setTitle('Shout Out: final scores');
        drawStandings(this, standings, {x: HOST_CENTER_X - 320, y: 110, width: 640, max: 8});
        addContinueButton(this, HOST_CENTER_X, HOST_HEIGHT - 60, 'Continue', {width: 300});
    }
}
