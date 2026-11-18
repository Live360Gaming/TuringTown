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
