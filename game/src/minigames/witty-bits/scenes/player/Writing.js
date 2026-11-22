import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';

const MAX_LENGTH = 80;

/** How the server tidies an answer (trimmed, single spaces), so the phone can recognise its own answer later. */
export const tidy = text => (text ?? '').replace(/\s+/g, ' ').trim();

/** The answers this phone wrote this game, so a matchup can mark the player's own answer. */
export function myAnswers(session) {
    session.wittyAnswers ??= new Set();
    return session.wittyAnswers;
}

// Write the answers to your prompts. Each answer is sent on its own, so a slow writer still gets the first one in.
export class WittyBitsPlayerWriting extends PlayerStage {
    constructor() {
        // The form has text boxes, so ESC must not drop the player out.
        super('WittyBitsPlayerWriting', {escapeLeaves: false});
    }

    build() {
        const {lastBit, round, assignments = []} = this.session.state.data ?? {};
        // A new game starts with a clean slate.
        if (round === 1) this.session.wittyAnswers = new Set();

        const me = this.session.you?.id;
        this.prompts = assignments.filter(a => a.playerIds.includes(me));
        this.sent = new Set();
        this.sending = false;

        this.title = this.addText(lastBit ? 'The Last Bit' : 'Be funny!', {fontStyle: 'bold'});
        this.statusText = this.addText('', {align: 'center'});

        if (this.prompts.length === 0) {
            this.statusText.setText('Sit back: the others are writing.');
            return;
        }

        this.buildForm();
        this.listen(this.session, 'progress', () => this.showWaiting());
    }

    // A real HTML form, so phones bring up their keyboard and scroll to the field being typed in.
    buildForm() {
        this.form = this.add.dom(0, 0, 'div');
        const root = this.form.node;
        root.className = 'tt-form';
        root.addEventListener('focusin', e => e.target.scrollIntoView?.({block: 'nearest'}));

        this.fields = this.prompts.map((prompt, index) => {
            const box = document.createElement('fieldset');
            box.className = 'tt-q';

            // textContent only: the prompt text comes from the server.
            const legend = document.createElement('legend');
            const eyebrow = document.createElement('span');
            eyebrow.className = 'tt-eyebrow';
            eyebrow.textContent = this.prompts.length > 1 ? `Prompt ${index + 1} / ${this.prompts.length}` : 'Prompt';
            const text = document.createElement('span');
            text.className = 'tt-q-text';
            text.textContent = prompt.text;
            legend.append(eyebrow, text);

            const input = document.createElement('textarea');
            input.rows = 2;
            input.maxLength = MAX_LENGTH;
            input.autocomplete = 'off';
            input.placeholder = 'Your funniest answer';
            input.className = 'tt-input';

            box.append(legend, input);
            root.append(box);
            return {prompt, input, box};
        });

        const send = document.createElement('button');
        send.textContent = 'Send';
        send.className = 'tt-submit';
        send.addEventListener('click', () => this.submit());
        root.append(send);
    }

    async submit() {
        if (this.sending) return;
        const todo = this.fields.filter(f => !this.sent.has(f.prompt.promptId) && tidy(f.input.value));
        if (todo.length === 0) {
            this.statusText.setText('Write an answer first.');
            return;
        }

        this.sending = true;
        this.statusText.setText('Sending...');
        try {
            for (const field of todo) {
                const text = tidy(field.input.value);
                await this.session.invoke('SubmitWittyAnswer', field.prompt.promptId, text);
                this.sent.add(field.prompt.promptId);
                myAnswers(this.session).add(text);
                field.box.remove();
            }

            if (this.sent.size === this.fields.length) {
                this.form.destroy();
                this.showWaiting();
                this.relayout();
            } else {
                this.statusText.setText('Sent! Now the other one.');
            }
        } catch (err) {
            this.statusText.setText(hubErrorMessage(err));
        } finally {
            this.sending = false;
        }
    }

    showWaiting() {
        if (!this.fields || this.sent.size < this.fields.length) return;
        const {done, total} = this.session.progress;
        this.statusText.setText(`All sent!\nWaiting for everyone${total > 0 ? ` (${done}/${total})` : ''}`);
    }

    layout(m) {
        this.title.setPosition(m.cx, m.top + 130 * m.s).setFontSize(m.fs(40, 22));
        const formOpen = this.form?.active;
        this.statusText.setPosition(m.cx, formOpen ? m.bottom - 50 * m.s : m.cy)
            .setFontSize(m.fs(26, 16)).setWordWrapWidth(m.panelW - 60 * m.s);

        if (formOpen) {
            const formTop = m.top + 175 * m.s;
            this.form.setOrigin(0.5, 0).setPosition(m.cx, formTop);
            this.form.node.style.width = `${m.panelW - 32}px`;
            this.form.node.style.maxHeight = `${Math.max(160, m.bottom - formTop - 70 * m.s)}px`;
            this.form.updateSize();
        }
    }
}
