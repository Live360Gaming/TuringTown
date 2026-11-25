import {addHoverFill, BTN} from '../../../../core/ui/Button.js';
import {createAvatar} from '../../../../core/characters.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {readableOn} from '../../../../core/theme.js';
import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {roundTitle} from '../../rounds.js';

const CARD_W = 240;
const CARD_H = 100;
const SELECTED = 0x39ff14;

// Tap the player you think is a bot. You can change your vote until time is up.
export class TuringTownPlayerVoting extends PlayerStage {
    constructor() {
        super('TuringTownPlayerVoting');
    }

    build() {
        this.title = this.addText(roundTitle(this.session.state), {fontStyle: 'bold'});
        this.statusText = this.addText('', {align: 'center'});
        this.cards = [];
        // The screen object is reused, so the last vote's list (now destroyed) must be forgotten.
        this.answerList = null;

        if (this.session.spectating) {
            this.statusText.setText('You were voted out. You can only watch now.');
            return;
        }

        this.statusText.setText(this.session.state.data?.kind === 'Final'
            ? 'Final vote. Tap who you think is a bot.' : 'Tap who you suspect. Nobody leaves yet.');
        const me = this.session.you;
        if (this.session.state.data?.kind === 'RapidFire') {
            this.showAnswerList(this.session.state.data);
            return;
        }
        this.session.roster
            .filter(player => player.alive && player.id !== me.id)
            .forEach(player => this.cards.push(this.createCard(player)));
    }

    /** Everyone else's five answers as a scrolling list; tapping a player's block suspects them. */
    showAnswerList({answers = []}) {
        this.statusText.setText('Tap who seems off. Nobody leaves yet.');
        const list = document.createElement('div');
        list.style.overflowY = 'auto';
        list.style.display = 'flex';
        list.style.flexDirection = 'column';
        list.style.gap = '10px';

        answers.filter(entry => entry.playerId !== this.session.you.id).forEach(entry => {
            const player = this.session.roster.find(p => p.id === entry.playerId);
            const block = document.createElement('button');
            block.type = 'button';
            block.className = 'tt-input';
            block.style.textAlign = 'left';
            block.style.cursor = 'pointer';
            // textContent only: aliases and answers are player text.
            const name = document.createElement('strong');
            name.textContent = entry.alias;
            name.style.color = readableOn(player?.color ?? '#ffffff');
            block.append(name, ...entry.answers.map((answer, i) => {
                const line = document.createElement('div');
                line.textContent = `${i + 1}. ${answer ?? '...'}`;
                return line;
            }));
            block.addEventListener('click', async () => {
                try {
                    await this.session.invoke('CastVote', entry.playerId);
                    [...list.children].forEach(child => child.style.outline = 'none');
                    block.style.outline = '4px solid #39ff14';
                    this.statusText.setText(`You suspect ${entry.alias}.`);
                } catch (err) {
                    this.statusText.setText(hubErrorMessage(err));
                }
            });
            list.append(block);
        });

        this.listEl = list;
        this.answerList = this.add.dom(0, 0, list);
    }

    createCard(player) {
        const bg = this.add.rectangle(0, 0, CARD_W, CARD_H, BTN)
            .setStrokeStyle(3, 0xffffff)
            .setInteractive({useHandCursor: true});
        const avatar = createAvatar(this, -CARD_W / 2 + 50, 0, 64, player);
        const percent = this.session.state.data?.meter?.find(level => level.playerId === player.id)?.percent;
        const text = percent ? `${player.alias}\n${percent}% suspected` : player.alias;
        const label = this.add.text(-CARD_W / 2 + 96, 0, text, {fontSize: '22px', color: readableOn(player.color, '#405FC2'), wordWrap: {width: CARD_W - 110}})
            .setOrigin(0, 0.5);
        const container = this.add.container(0, 0, [bg, avatar, label]);

        addHoverFill(bg);
        bg.on('pointerup', () => this.vote(player, bg));

        return {container, bg};
    }

    async vote(player, bg) {
        try {
            await this.session.invoke('CastVote', player.id);
            this.cards.forEach(card => card.bg.setStrokeStyle(3, 0xffffff));
            bg.setStrokeStyle(6, SELECTED);
            this.statusText.setText(`You voted for ${player.alias}.\nYou can change it until time is up.`);
        } catch (err) {
            this.statusText.setText(hubErrorMessage(err));
        }
    }

    layout(m) {
        this.title.setPosition(m.cx, m.top + 120 * m.s).setFontSize(m.fs(36, 22));
        this.statusText.setPosition(m.cx, m.top + 175 * m.s).setFontSize(m.fs(22, 16)).setWordWrapWidth(m.panelW - 50 * m.s);

        if (this.answerList) {
            const top = m.top + 215 * m.s;
            const height = m.bottom - 20 * m.s - top;
            this.listEl.style.width = `${m.panelW - 32 * m.s}px`;
            this.listEl.style.height = `${height}px`;
            this.answerList.setPosition(m.cx, top + height / 2).updateSize();
        }

        // Two columns of cards.
        const colGap = 16 * m.s;
        const rowGap = 14 * m.s;
        const cardW = CARD_W * m.s;
        const cardH = CARD_H * m.s;
        const firstY = m.top + 270 * m.s;
        this.cards.forEach(({container}, i) => {
            const col = i % 2;
            const row = Math.floor(i / 2);
            container.setScale(m.s).setPosition(
                m.cx + (col === 0 ? -1 : 1) * (cardW / 2 + colGap / 2),
                firstY + row * (cardH + rowGap),
            );
        });
    }
}
