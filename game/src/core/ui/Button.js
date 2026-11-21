import {COLORS, HEX, textStyle} from '../theme.js';

export const BTN = HEX.blue;
export const BTN_HOVER = 0x5272d6;
export const BTN_DISABLED = HEX.surfaceRaised;
export const PANEL = HEX.surface;

/** Lights up a rectangle while the pointer is over it. For cards and chips that are not full buttons. */
export function addHoverFill(rect) {
    return rect
        .on('pointerover', () => rect.setFillStyle(BTN_HOVER))
        .on('pointerout', () => rect.setFillStyle(BTN));
}

// Fill, hover fill and label color for each button variant.
const VARIANTS = {
    primary: {fill: HEX.pink, hover: 0xff86cc, border: 0xffffff, label: COLORS.bgDeep, role: 'button'},
    secondary: {fill: BTN, hover: BTN_HOVER, border: HEX.cyan, label: COLORS.text, role: 'buttonAlt'},
    ghost: {fill: null, hover: HEX.surfaceRaised, border: HEX.border, label: COLORS.text, role: 'buttonAlt'},
};

/**
 * Rounded label button. Returns a container (with setEnabled/setLabel) so it can be grouped into panels.
 * `variant` is 'primary' (pink, for the one main action), 'secondary' (blue, the default) or 'ghost'.
 */
export function createTextButton(scene, x, y, label, onClick, {width = 300, height = 70, fontSize = '32px', variant = 'secondary'} = {}) {
    const look = VARIANTS[variant] ?? VARIANTS.secondary;
    let enabled = true;
    let hover = false;
    const radius = Math.min(18, height / 2);

    const gfx = scene.add.graphics();
    const draw = () => {
        gfx.clear();
        const fill = !enabled ? BTN_DISABLED : hover ? look.hover : look.fill;
        if (fill !== null) gfx.fillStyle(fill, 1).fillRoundedRect(-width / 2, -height / 2, width, height, radius);
        else if (hover && enabled) gfx.fillStyle(look.hover, 1).fillRoundedRect(-width / 2, -height / 2, width, height, radius);
        gfx.lineStyle(2, enabled ? look.border : HEX.border, enabled ? 1 : 0.6)
            .strokeRoundedRect(-width / 2, -height / 2, width, height, radius);
    };

    // The hit area is a zone, so it stays rectangular and at least as big as the drawn button.
    const hit = scene.add.zone(0, 0, width, height)
        .setInteractive({useHandCursor: true})
        .on('pointerover', () => { hover = true; draw(); })
        .on('pointerout', () => { hover = false; draw(); })
        .on('pointerup', () => enabled && onClick());
    const text = scene.add.text(0, 0, label, textStyle(look.role, {fontSize, color: look.label})).setOrigin(0.5);
    const button = scene.add.container(x, y, [gfx, hit, text]);
    draw();

    button.setEnabled = on => {
        enabled = on;
        hit.input.cursor = on ? 'pointer' : 'default';
        text.setAlpha(on ? 1 : 0.5).setColor(on ? look.label : COLORS.textDim);
        draw();
    };
    button.setLabel = value => text.setText(value);
    return button;
}

/**
 * Image button with hover / press feedback that fires on release.
 * `getBaseScale` returns the button's current layout scale, so the feedback scales with it.
 */
export function createImageButton(scene, textureKey, onClick, getBaseScale = () => 1) {
    const btn = scene.add.image(0, 0, textureKey)
        .setInteractive({useHandCursor: true});

    // Hover effect (desktop)
    btn.on('pointerover', () => {
        btn.setScale(1.05 * getBaseScale());
        btn.setTint(0xffffff);
    });

    btn.on('pointerout', () => {
        btn.setScale(getBaseScale());
        btn.clearTint();
    });

    // Press down effect
    btn.on('pointerdown', () => {
        btn.setScale(0.96 * getBaseScale());
        btn.setTint(0xcccccc);
    });

    // Trigger action on release
    btn.on('pointerup', () => {
        btn.setScale(1.05 * getBaseScale());
        btn.clearTint();
        onClick();
    });

    return btn;
}
