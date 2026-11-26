import {HOST_CENTER_X, HOST_HEIGHT} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {createAvatar} from '../../../../core/characters.js';
import {addNameplate} from '../../../../core/ui/Nameplate.js';
import {COLORS, textStyle} from '../../../../core/theme.js';
import {drawOptions, drawQuestion, questionTitle} from './board.js';

// A question on the TV: the question, then the four answers. "Get ready..." until buzzing opens, then "BUZZ IN!".
export class ShoutOutHostQuestion extends HostStage {
    constructor() {
        super('ShoutOutHostQuestion');
    }

    build() {
        const data = this.session.state.data ?? {};
        this.setTitle(questionTitle(data));
        const bottom = drawQuestion(this, data.text ?? '');
        drawOptions(this, data.options ?? [], bottom + 30);

        const out = new Set(data.out ?? []);
        this.status = this.add.text(HOST_CENTER_X, HOST_HEIGHT - 60, '', textStyle('title', {fontSize: '40px'})).setOrigin(0.5);
        this.outText = this.add.text(HOST_CENTER_X, HOST_HEIGHT - 20, '', textStyle('meta', {fontSize: '18px', color: COLORS.textDim})).setOrigin(0.5);
        if (out.size) {
            const names = this.session.roster.filter(p => out.has(p.id)).map(p => p.alias);
            this.outText.setText(`Already tried: ${names.join(', ')}`);
        }

        this.time.addEvent({delay: 100, loop: true, callback: () => this.tick()});
        this.tick();
    }

    tick() {
        const opensAt = this.session.state.data?.buzzOpensAtUtcMs ?? 0;
        const open = this.session.serverNow() >= opensAt;
        this.status.setText(open ? 'BUZZ IN!' : 'Get ready...').setColor(open ? COLORS.green : COLORS.gold);
    }
}

// Someone buzzed: their avatar and name, while they pick an answer on their phone.
export class ShoutOutHostBuzzed extends HostStage {
    constructor() {
        super('ShoutOutHostBuzzed');
    }

    build() {
        const data = this.session.state.data ?? {};
        this.setTitle(questionTitle(data));
        const bottom = drawQuestion(this, data.text ?? '', 180);
        drawOptions(this, data.options ?? [], bottom + 20);

        const player = this.session.roster.find(p => p.id === data.buzzerId);
        const y = HOST_HEIGHT - 70;
        if (player) {
            createAvatar(this, HOST_CENTER_X - 230, y, 100, player);
            addNameplate(this, HOST_CENTER_X - 230, y + 56, player, {maxWidth: 200, fontSize: 18});
        }
        this.add.text(HOST_CENTER_X + 60, y, `${data.buzzerAlias ?? 'Someone'} buzzed in!`, textStyle('title', {fontSize: '36px', color: COLORS.pink}))
            .setOrigin(0.5);
        this.sound.play('message_sfx2');
    }
}
