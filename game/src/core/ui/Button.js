export const BTN = 0x3355aa;
export const BTN_HOVER = 0x4466cc;
export const BTN_DISABLED = 0x2a2f4d;
export const PANEL = 0x222244;

/** Lights up a rectangle while the pointer is over it. For cards and chips that are not full buttons. */
export function addHoverFill(rect) {
    return rect
        .on('pointerover', () => rect.setFillStyle(BTN_HOVER))
        .on('pointerout', () => rect.setFillStyle(BTN));
}

/** Simple rectangle + label button. Returns a container (with setEnabled/setLabel) so it can be grouped into panels. */
export function createTextButton(scene, x, y, label, onClick, {width = 300, height = 70, fontSize = '32px'} = {}) {
    let enabled = true;
    const bg = scene.add.rectangle(0, 0, width, height, BTN)
        .setStrokeStyle(2, 0xffffff)
        .setInteractive({useHandCursor: true})
        .on('pointerover', () => enabled && bg.setFillStyle(BTN_HOVER))
        .on('pointerout', () => bg.setFillStyle(enabled ? BTN : BTN_DISABLED))
        .on('pointerup', () => enabled && onClick());
    const text = scene.add.text(0, 0, label, {fontSize, color: '#ffffff'}).setOrigin(0.5);
    const button = scene.add.container(x, y, [bg, text]);
    button.setEnabled = on => {
        enabled = on;
        bg.setFillStyle(on ? BTN : BTN_DISABLED);
        bg.input.cursor = on ? 'pointer' : 'default';
        text.setAlpha(on ? 1 : 0.4);
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
