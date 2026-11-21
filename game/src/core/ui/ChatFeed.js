import {createAvatar} from '../characters.js';
import {COLORS, readableOn} from '../theme.js';

/**
 * A chat log drawn from the bottom of a rectangle upwards, newest at the bottom. Older lines that no longer
 * fit are simply not drawn. Used on the TV and on phones, so size and position can change (see `resize`).
 *
 * A message is {alias, color, characterId, text} as sent by the server.
 */
export class ChatFeed {
    /**
     * `highlightPlayerId`: messages that @mention this player are shown in gold, so they stand out. A message is
     * {alias, color, characterId, text, mentions: [playerId]}.
     */
    constructor(scene, {x = 0, y = 0, width = 400, height = 400, fontSize = 22, avatarSize = 44, highlightPlayerId = null} = {}) {
        this.scene = scene;
        this.messages = [];
        this.items = [];
        this.highlightPlayerId = highlightPlayerId;
        this.box = {x, y, width, height, fontSize, avatarSize};
    }

    resize(box) {
        this.box = {...this.box, ...box};
        this.render();
    }

    setMessages(messages) {
        this.messages = [...messages];
        this.render();
    }

    add(message) {
        this.messages.push(message);
        this.render();
    }

    render() {
        this.items.forEach(item => item.destroy());
        this.items = [];

        const {x, y, width, height, fontSize, avatarSize} = this.box;
        const gap = fontSize * 0.5;
        const textX = x + avatarSize + gap;
        const textWidth = Math.max(40, width - avatarSize - gap);
        let bottom = y + height;

        for (let i = this.messages.length - 1; i >= 0; i--) {
            const message = this.messages[i];

            const alias = this.scene.add.text(textX, 0, message.alias, {fontSize: `${fontSize}px`, fontStyle: 'bold', color: readableOn(message.color, COLORS.surface)})
                .setResolution(window.devicePixelRatio);
            const toMe = this.highlightPlayerId != null && message.mentions?.includes(this.highlightPlayerId);
            const body = this.scene.add.text(textX, 0, message.text, {
                fontSize: `${fontSize}px`, color: toMe ? '#ffd54a' : '#ffffff', fontStyle: toMe ? 'bold' : 'normal',
                wordWrap: {width: textWidth},
            }).setResolution(window.devicePixelRatio);

            const itemHeight = Math.max(avatarSize, alias.height + body.height) + gap;
            const top = bottom - itemHeight;
            if (top < y) {
                alias.destroy();
                body.destroy();
                break;
            }

            alias.setY(top);
            body.setY(top + alias.height);
            const avatar = createAvatar(this.scene, x + avatarSize / 2, top + avatarSize / 2, avatarSize, message);
            this.items.push(alias, body, avatar);
            bottom = top;
        }
    }

    destroy() {
        this.items.forEach(item => item.destroy());
        this.items = [];
    }
}
