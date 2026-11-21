import {PlayerStage} from '../../../../core/stage/PlayerStage.js';
import {createTextButton} from '../../../../core/ui/Button.js';
import {ChatFeed} from '../../../../core/ui/ChatFeed.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {buzz, hapticsEnabled, hapticsSupported, setHapticsEnabled} from '../../../../core/haptics.js';

// The anonymous chat on a phone. Only your alias is ever shown. Someone who has been voted out can read but not write.
export class TuringTownPlayerDiscussion extends PlayerStage {
    constructor() {
        super('TuringTownPlayerDiscussion', {escapeLeaves: false});
    }

    build() {
        this.sending = false;
        this.canWrite = !this.session.spectating;

        this.title = this.addText(`Round ${this.session.state.round}: Discuss`, {fontStyle: 'bold'}).setOrigin(0, 0.5);

        // Each phone can turn its own buzz off. Hidden where the browser cannot vibrate at all (iPhones).
        this.buzzBtn = null;
        if (hapticsSupported()) {
            const label = () => `Buzz: ${hapticsEnabled() ? 'On' : 'Off'}`;
            this.buzzBtn = createTextButton(this, 0, 0, label(), () => {
                const on = setHapticsEnabled(!hapticsEnabled());
                this.buzzBtn.setLabel(label());
                if (on) buzz(40);
            }, {width: 110, height: 44, fontSize: '18px', variant: 'ghost'});
        }

        this.statusText = this.addText(this.canWrite ? '' : 'You were voted out. You can only watch now.', {align: 'center'});

        // Lines that @mention you are shown in gold.
        this.feed = new ChatFeed(this, {highlightPlayerId: this.session.you?.id ?? null});
        this.feed.setMessages(this.session.transcript);
        this.listen(this.session, 'chat', message => {
            this.feed.add(message);
            // Everyone's buzz is identical for every message, so it never hints at who is a bot.
            if (message.playerId !== this.session.you?.id) buzz(60);
            this.notifyIfMentioned(message);
        });
        // "Purple Mango is typing..." for everyone else, bots included.
        const showTyping = typing => this.feed.setTyping(
            this.session.roster.filter(p => typing.has(p.id) && p.id !== this.session.you?.id).map(p => p.alias));
        this.listen(this.session, 'typing', showTyping);
        showTyping(new Set(this.session.typing.keys()));
        this.events.once('shutdown', () => {
            this.feed.destroy();
            if (this.typingReported) this.session.reportTyping(false);
        });

        if (this.canWrite) {
            // A real HTML input so phones bring up their keyboard. It sits in a wrapper that also holds the
            // @mention popup, so the popup moves with the box.
            this.chatInput = this.add.dom(0, 0, 'div');
            const box = this.chatInput.node;
            box.className = 'tt-chatbox';

            const input = document.createElement('input');
            input.placeholder = 'Say something... (@ to mention)';
            input.className = 'tt-input';
            input.maxLength = 200;
            input.autocomplete = 'off';
            input.setAttribute('role', 'combobox');
            input.setAttribute('aria-autocomplete', 'list');
            input.setAttribute('aria-expanded', 'false');
            this.inputEl = input;

            this.mentionList = document.createElement('ul');
            this.mentionList.className = 'tt-mention-list';
            this.mentionList.id = 'tt-mention-list';
            this.mentionList.setAttribute('role', 'listbox');
            this.mentionList.hidden = true;
            input.setAttribute('aria-controls', this.mentionList.id);
            box.append(this.mentionList, input);

            this.mention = null;
            input.addEventListener('keydown', e => this.onKeyDown(e));
            input.addEventListener('input', () => {
                this.reportTyping();
                this.updateMentions();
            });
            // Moving the cursor into or out of an "@name" opens or closes the popup.
            input.addEventListener('click', () => this.updateMentions());
            input.addEventListener('keyup', e => {
                if (e.key === 'ArrowLeft' || e.key === 'ArrowRight' || e.key === 'Home' || e.key === 'End') this.updateMentions();
            });
            input.addEventListener('blur', () => this.closeMentions());

            this.sendBtn = createTextButton(this, 0, 0, 'Send', () => this.send(), {width: 110, height: 56, fontSize: '26px'});
        }
    }

    onKeyDown(e) {
        const m = this.mention;
        if (m) {
            if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
                e.preventDefault();
                const step = e.key === 'ArrowDown' ? 1 : -1;
                this.highlightMention((m.index + step + m.matches.length) % m.matches.length);
                return;
            }
            if (e.key === 'Enter' || e.key === 'Tab') {
                e.preventDefault();
                this.pickMention(m.matches[m.index]);
                return;
            }
            if (e.key === 'Escape') {
                e.preventDefault();
                this.closeMentions();
                return;
            }
        }
        if (e.key === 'Enter') this.send();
    }

    /**
     * Opens the popup while the cursor is right after an "@" plus the start of a name, for example "hi @pur|".
     * The "@" must start the text or follow a space, so an email address does not open it. Aliases contain a
     * space ("Purple Mango"), so the typed part may too. It only stays open while some name still matches.
     */
    updateMentions() {
        const input = this.inputEl;
        const caret = input.selectionStart ?? input.value.length;
        if (input.selectionEnd !== caret) return this.closeMentions();

        const before = input.value.slice(0, caret);
        const at = before.lastIndexOf('@');
        if (at < 0 || (at > 0 && !/\s/.test(before[at - 1]))) return this.closeMentions();

        const query = before.slice(at + 1).toLowerCase();
        const youId = this.session.you?.id;
        const matches = this.session.roster
            .filter(p => p.alive && p.id !== youId)
            .filter(p => {
                const alias = p.alias.toLowerCase();
                // "pur", "mango" and "purple m" all find "Purple Mango".
                return alias.startsWith(query) || alias.split(/\s+/).some(word => word.startsWith(query));
            });
        if (matches.length === 0) return this.closeMentions();

        // Keep the highlighted name when it still matches, so typing does not make the choice jump around.
        const keepId = this.mention?.matches[this.mention.index]?.id;
        const index = Math.max(0, matches.findIndex(p => p.id === keepId));
        this.mention = {start: at, end: caret, matches, index};
        this.renderMentions();
    }

    renderMentions() {
        const {matches, index} = this.mention;
        const list = this.mentionList;
        list.replaceChildren(...matches.map((player, i) => {
            const item = document.createElement('li');
            item.className = 'tt-mention';
            item.id = `tt-mention-${i}`;
            item.setAttribute('role', 'option');
            item.style.setProperty('--tt-mention-color', player.color);
            // textContent only: the alias comes from the server.
            const dot = document.createElement('span');
            dot.className = 'tt-mention-dot';
            const name = document.createElement('span');
            name.textContent = player.alias;
            item.append(dot, name);
            // pointerdown + preventDefault keeps focus in the box, so the phone keyboard stays up.
            item.addEventListener('pointerdown', e => {
                e.preventDefault();
                this.pickMention(player);
            });
            item.addEventListener('pointerenter', () => this.highlightMention(i));
            return item;
        }));
        list.hidden = false;
        this.inputEl.setAttribute('aria-expanded', 'true');
        this.highlightMention(index);
    }

    highlightMention(index) {
        this.mention.index = index;
        [...this.mentionList.children].forEach((item, i) => item.setAttribute('aria-selected', String(i === index)));
        this.mentionList.children[index]?.scrollIntoView?.({block: 'nearest'});
        this.inputEl.setAttribute('aria-activedescendant', `tt-mention-${index}`);
    }

    closeMentions() {
        this.mention = null;
        if (!this.mentionList) return;
        this.mentionList.hidden = true;
        this.mentionList.replaceChildren();
        this.inputEl.setAttribute('aria-expanded', 'false');
        this.inputEl.removeAttribute('aria-activedescendant');
    }

    /** Swaps the typed "@pur" for "@Purple Mango ". */
    pickMention(player) {
        const {start, end} = this.mention;
        const input = this.inputEl;
        input.setRangeText(`@${player.alias} `, start, end, 'end');
        input.focus();
        this.closeMentions();
        this.reportTyping();
    }

    /** Someone @mentioned you: say so, and buzz the phone if it can. */
    notifyIfMentioned(message) {
        const me = this.session.you;
        if (!me || message.playerId === me.id || !message.mentions?.includes(me.id)) return;

        buzz([120, 60, 120]);
        this.statusText.setText(`${message.alias} mentioned you`).setColor('#ffd54a');
        this.time.delayedCall(4000, () => this.statusText.setText('').setColor('#ffffff'));
    }

    /** Tells the room while there is text in the box. Repeats every few seconds, so a stuck flag is refreshed or expires. */
    reportTyping(forceStop = false) {
        const typing = !forceStop && this.inputEl.value.trim().length > 0;
        const now = Date.now();
        if (typing && this.typingReported && now - this.typingSentAt < 4000) return;
        if (!typing && !this.typingReported) return;
        this.typingReported = typing;
        this.typingSentAt = now;
        this.session.reportTyping(typing);
    }

    async send() {
        const input = this.inputEl;
        const text = input.value.trim();
        if (!text || this.sending) return;
        this.closeMentions();

        this.sending = true;
        try {
            await this.session.invoke('SendChat', text);
            input.value = '';
            this.reportTyping(true);
            this.statusText.setText('');
        } catch (err) {
            this.statusText.setText(hubErrorMessage(err));
        } finally {
            this.sending = false;
        }
    }

    layout(m) {
        const pad = 24 * m.s;
        const feedTop = m.top + 150 * m.s;
        const inputTop = m.bottom - 90 * m.s;

        // The @mention popup floats over the feed, so the feed can use all the room down to the chat box.
        const feedBottom = inputTop - 22 * m.s;

        this.title.setPosition(m.left + pad, m.top + 120 * m.s).setFontSize(m.fs(34, 22));
        this.buzzBtn?.setPosition(m.right - pad - 55, m.top + 120 * m.s);
        // On a narrow phone, shrink the title so it never runs under the button.
        const buttonsLeft = this.buzzBtn ? this.buzzBtn.x - 55 : m.right;
        this.fitText(this.title, m.fs(34, 22), buttonsLeft - 10 - (m.left + pad));
        this.statusText.setPosition(m.cx, m.top + 175 * m.s).setFontSize(m.fs(22, 16)).setWordWrapWidth(m.panelW - 2 * pad);

        this.feed.resize({
            x: m.left + pad, y: feedTop + 36 * m.s, width: m.panelW - 2 * pad, height: feedBottom - feedTop - 36 * m.s,
            fontSize: m.fs(20, 15), avatarSize: Math.max(32, 40 * m.s),
        });

        if (this.canWrite && this.chatInput.active) {
            const sendWidth = 110 * m.s;
            const inputWidth = m.panelW - 3 * pad - sendWidth;
            this.chatInput.setPosition(m.left + pad + inputWidth / 2, inputTop + 30 * m.s);
            this.chatInput.node.style.width = `${inputWidth}px`;
            this.inputEl.style.fontSize = `${Math.max(16, 24 * m.s)}px`;
            // The popup can grow up to the top of the feed, then scrolls.
            this.mentionList.style.maxHeight = `${Math.max(120, feedBottom - feedTop)}px`;
            this.chatInput.updateSize();
            this.sendBtn.setPosition(m.right - pad - sendWidth / 2, inputTop + 30 * m.s).setScale(Math.max(m.s, 0.8));
        }
    }
}
