import {Scene} from "phaser";
import {HOST_CENTER_X} from "../../../constants.js";
import {COLORS, HEX, prefersReducedMotion, textStyle} from "../../../core/theme.js";
import {getSession} from "../../../core/session.js";

const SHOW_MS = 4500;
const MAX_VISIBLE = 3;
const TOP = 28;
const ROW_H = 54;

const LOOKS = {
    disconnected: {color: COLORS.pink, hex: HEX.pink, text: name => `${name} disconnected`},
    reconnected: {color: COLORS.green, hex: HEX.green, text: name => `${name} reconnected`},
    removed: {color: COLORS.textMuted, hex: HEX.border, text: name => `${name} left the lobby`},
};

// Banners across the top of the TV that say who dropped, came back or was cleared. It runs for as long as the host's
// session does (started by GameSession.attachDirector), so it sits on top of the lobby and every game screen.
// It never takes input, so it cannot get in the way of a screen below it.
export class PresenceToast extends Scene {
    constructor() {
        super("PresenceToast");
    }

    create() {
        this.input.enabled = false;
        this.banners = [];
        this.session = getSession(this);
        this.session?.on('presence', this.show, this);
        this.events.once('shutdown', () => this.session?.off('presence', this.show, this));
    }

    show({player, status}) {
        const look = LOOKS[status];
        if (!look) return;

        const label = this.add.text(0, 0, look.text(player.alias), textStyle('instruction', {fontSize: '24px', color: look.color})).setOrigin(0.5);
        const w = label.width + 56;
        const h = 42;
        const bg = this.add.graphics()
            .fillStyle(HEX.bgDeep, 0.92).fillRoundedRect(-w / 2, -h / 2, w, h, h / 2)
            .lineStyle(2, look.hex, 1).strokeRoundedRect(-w / 2, -h / 2, w, h, h / 2);
        const banner = this.add.container(HOST_CENTER_X, TOP - ROW_H, [bg, label]).setDepth(1000).setAlpha(0);

        // A newer message for the same player replaces the older one instead of stacking.
        this.banners.filter(b => b.playerId === player.id).forEach(b => this.dismiss(b, true));
        this.banners.push(banner);
        banner.playerId = player.id;
        while (this.banners.length > MAX_VISIBLE) this.dismiss(this.banners[0], true);
        this.arrange();

        banner.timer = this.time.delayedCall(SHOW_MS, () => this.dismiss(banner));
    }

    dismiss(banner, instant = false) {
        const index = this.banners.indexOf(banner);
        if (index === -1) return;
        this.banners.splice(index, 1);
        banner.timer?.remove();
        if (instant || prefersReducedMotion()) banner.destroy();
        else this.tweens.add({targets: banner, alpha: 0, duration: 250, onComplete: () => banner.destroy()});
        this.arrange();
    }

    arrange() {
        this.banners.forEach((banner, i) => {
            const y = TOP + 21 + i * ROW_H;
            if (prefersReducedMotion()) banner.setPosition(HOST_CENTER_X, y).setAlpha(1);
            else this.tweens.add({targets: banner, y, alpha: 1, duration: 200, ease: 'Sine.easeOut'});
        });
    }
}
