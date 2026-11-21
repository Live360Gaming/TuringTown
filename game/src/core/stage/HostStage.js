import {Scene} from 'phaser';
import {HOST_CENTER_X, HOST_HEIGHT, HOST_WIDTH} from '../../constants.js';
import {enterFixedSize, fitImageToScreen} from '../scaling.js';
import {getSession, leaveToMenu} from '../session.js';
import {attachCountdown} from '../countdown.js';
import {createAvatar} from '../characters.js';
import {listen} from './listen.js';

/**
 * Base for a screen on the TV during a game: the fixed 16:9 canvas, background, a title, the stage countdown,
 * and ESC to leave. Subclasses fill in `build()`. The scene reads everything from `this.session`.
 */
export class HostStage extends Scene {
    create() {
        enterFixedSize(this.scale, HOST_WIDTH, HOST_HEIGHT);

        this.session = getSession(this);
        if (this.session?.role !== 'host') {
            this.scene.start('MainMenu');
            return;
        }

        fitImageToScreen(this, 'background_tv');
        this.title = this.add.text(HOST_CENTER_X, 48, '', {fontSize: '44px', color: '#ffffff'}).setOrigin(0.5);
        this.timerText = this.add.text(HOST_WIDTH - 40, 48, '', {fontSize: '40px', color: '#ffffff'}).setOrigin(1, 0.5);
        attachCountdown(this, this.session, this.timerText);

        this.input.keyboard.on('keydown-ESC', () => leaveToMenu(this));

        this.build();
    }

    /** Create this screen's objects. */
    build() {}

    setTitle(text) {
        this.title.setText(text);
    }

    /** Subscribe to an emitter for as long as this scene is running. */
    listen(emitter, event, handler) {
        listen(this, emitter, event, handler);
    }

    /**
     * A row of everyone's avatar and alias, kept up to date as players come, go or are voted out.
     * Returns a function that redraws it.
     */
    createRosterRow(y, {size = 84, filter = () => true} = {}) {
        let items = [];
        const draw = () => {
            items.forEach(item => item.destroy());
            items = [];

            const players = this.session.roster.filter(filter);
            const gap = Math.min(170, (HOST_WIDTH - 120) / Math.max(players.length, 1));
            const startX = HOST_CENTER_X - ((players.length - 1) * gap) / 2;

            players.forEach((player, i) => {
                const x = startX + i * gap;
                items.push(createAvatar(this, x, y, size, player, {dim: !player.alive}));
                items.push(this.add.text(x, y + size / 2 + 16, player.alias, {fontSize: '20px', color: player.color})
                    .setOrigin(0.5)
                    .setAlpha(player.alive ? 1 : 0.4));
            });
        };

        this.listen(this.session, 'roster', draw);
        draw();
        return draw;
    }
}
