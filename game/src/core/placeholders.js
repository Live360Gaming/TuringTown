/**
 * Makes a labelled placeholder image under `key`, unless a texture with that key already exists. So real art
 * can replace a placeholder later just by loading an image with the same key.
 */
export function ensurePlaceholderImage(scene, key, {width = 640, height = 360, label = key, color = '#3355aa'} = {}) {
    if (scene.textures.exists(key)) return key;

    const texture = scene.textures.createCanvas(key, width, height);
    const ctx = texture.getContext();

    ctx.fillStyle = color;
    ctx.fillRect(0, 0, width, height);

    // A faint cross marks it as a placeholder.
    ctx.strokeStyle = 'rgba(255,255,255,0.25)';
    ctx.lineWidth = 4;
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.lineTo(width, height);
    ctx.moveTo(width, 0);
    ctx.lineTo(0, height);
    ctx.stroke();

    ctx.strokeStyle = '#ffffff';
    ctx.lineWidth = 8;
    ctx.strokeRect(4, 4, width - 8, height - 8);

    ctx.fillStyle = '#ffffff';
    ctx.font = `bold ${Math.round(height / 8)}px sans-serif`;
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.fillText(label, width / 2, height / 2);

    texture.refresh();
    return key;
}
