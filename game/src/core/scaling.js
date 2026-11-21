import * as Phaser from 'phaser';

// Keeps an image stretched to exactly fill the game canvas (nothing cropped, no bars),
// and re-fills it whenever the canvas is resized.
export function fitImageToScreen(scene, key) {
    const image = scene.add.image(0, 0, key);

    const cover = () => {
        const {width, height} = scene.scale;
        image.setPosition(width / 2, height / 2)
            .setDisplaySize(width, height);
    };

    cover();
    scene.scale.on('resize', cover);
    scene.events.once('shutdown', () => scene.scale.off('resize', cover));
    return image;
}

// The notch / home-indicator margins in px ({top, right, bottom, left}, all 0 on screens without them).
export function safeInsets() {
    const none = {top: 0, right: 0, bottom: 0, left: 0};
    try {
        const probe = document.createElement('div');
        probe.style.cssText = 'position:fixed;top:0;left:0;visibility:hidden;pointer-events:none;' +
            'padding:env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left)';
        document.body.appendChild(probe);
        const style = getComputedStyle(probe);
        const insets = {
            top: parseFloat(style.paddingTop) || 0, right: parseFloat(style.paddingRight) || 0,
            bottom: parseFloat(style.paddingBottom) || 0, left: parseFloat(style.paddingLeft) || 0,
        };
        probe.remove();
        return insets;
    } catch {
        return none;
    }
}

// Make the canvas fill the whole browser window (no letterboxing).
export function enterFullWindow(scale) {
    scale.scaleMode = Phaser.Scale.RESIZE;
    // FIT locks the display to the old aspect ratio and leaves a CSS size on the canvas; clear both.
    scale.displaySize.setAspectMode(Phaser.Structs.Size.NONE);
    scale.canvas.style.width = '';
    scale.canvas.style.height = '';
    scale.refresh();
}

// Go back to a fixed resolution that is scaled to fit the window (letterboxed).
export function enterFixedSize(scale, width, height) {
    scale.scaleMode = Phaser.Scale.FIT;
    scale.displaySize.setAspectMode(Phaser.Structs.Size.FIT);
    scale.setGameSize(width, height);
    scale.refresh();
}
