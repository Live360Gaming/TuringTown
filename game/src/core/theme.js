// Single source of truth for the game's look: colors, fonts and text styles. The same values are repeated as
// --tt-* custom properties in public/style.css for the one HTML form (keep the two in step).

import * as Phaser from 'phaser';

export const COLORS = {
    bg: '#15172B',
    bgDeep: '#0B0D1A',
    surface: '#242342',
    surfaceRaised: '#2F2D55',
    border: '#4A4E7A',
    text: '#F8F8FF',
    textMuted: '#C8CEE1',
    textDim: '#8E96B8',
    cyan: '#54F5E6',
    pink: '#FF65BD',
    gold: '#FFD476',
    blue: '#405FC2',
    green: '#39FF14',
    danger: '#FF5C6C',
};

/** The same palette as numbers, for Phaser shapes (0x15172b). */
export const HEX = Object.fromEntries(Object.entries(COLORS).map(([key, css]) => [key, parseInt(css.slice(1), 16)]));

export const FONTS = {
    display: '"Bungee", "Arial Black", Impact, sans-serif',
    ui: '"Nunito Sans", "Segoe UI", Roboto, Arial, sans-serif',
    mono: '"Space Mono", Consolas, Menlo, "Courier New", monospace',
};

// Reference sizes are for the 1280x720 TV; phones scale them with fontPx().
const ROLES = {
    title: {fontFamily: FONTS.display, fontSize: 40, color: COLORS.text, stroke: COLORS.bgDeep, strokeThickness: 6},
    instruction: {fontFamily: FONTS.ui, fontStyle: '800', fontSize: 26, color: COLORS.text, stroke: COLORS.bgDeep, strokeThickness: 4},
    body: {fontFamily: FONTS.ui, fontStyle: '700', fontSize: 22, color: COLORS.textMuted},
    bodyOnArt: {fontFamily: FONTS.ui, fontStyle: '700', fontSize: 22, color: COLORS.text, stroke: COLORS.bgDeep, strokeThickness: 4},
    name: {fontFamily: FONTS.ui, fontStyle: '800', fontSize: 22, color: COLORS.text},
    code: {fontFamily: FONTS.mono, fontStyle: '700', fontSize: 52, color: COLORS.gold, stroke: COLORS.bgDeep, strokeThickness: 4},
    timer: {fontFamily: FONTS.mono, fontStyle: '700', fontSize: 44, color: COLORS.text, stroke: COLORS.bgDeep, strokeThickness: 4},
    meta: {fontFamily: FONTS.mono, fontStyle: '700', fontSize: 20, color: COLORS.cyan},
    button: {fontFamily: FONTS.display, fontSize: 28, color: COLORS.text},
    buttonAlt: {fontFamily: FONTS.ui, fontStyle: '800', fontSize: 22, color: COLORS.text},
    bubble: {fontFamily: FONTS.ui, fontStyle: '700', fontSize: 22, color: '#1A1A2E'},
};

/** A Phaser text style for a role, with optional overrides. Sizes are numbers (px) or strings ('24px'). */
export function textStyle(role, overrides = {}) {
    const style = {...(ROLES[role] ?? ROLES.body), ...overrides};
    if (typeof style.fontSize === 'number') style.fontSize = `${style.fontSize}px`;
    return style;
}

/** A font size scaled by `s` but never below `min` (px), so phone text stays readable. */
export function fontPx(px, s = 1, min = 14) {
    return Math.max(min, Math.round(px * s));
}

function toRgb(hex) {
    const n = parseInt(hex.replace('#', ''), 16);
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function luminance([r, g, b]) {
    const lin = c => {
        const v = c / 255;
        return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
    };
    return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

export function contrast(fg, bg) {
    const a = luminance(toRgb(fg));
    const b = luminance(toRgb(bg));
    return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

/** The player's color, lightened toward white until it reads at 4.5:1 on `bg`. Used where colored text stays. */
export function readableOn(hex, bg = COLORS.bg) {
    let rgb = toRgb(hex);
    const toHex = c => `#${c.map(v => Math.round(v).toString(16).padStart(2, '0')).join('')}`;
    for (let i = 0; i < 20 && contrast(toHex(rgb), bg) < 4.5; i++) {
        rgb = rgb.map(v => v + (255 - v) * 0.12);
    }
    return toHex(rgb);
}

/**
 * Makes every `scene.add.text` default to the UI font at weight 700 (instead of Phaser's thin Courier). Styles that
 * set their own fontFamily/fontStyle are untouched. Call once, before the Game is created.
 */
export function installTextDefaults() {
    const proto = Phaser.GameObjects.GameObjectFactory.prototype;
    if (proto.text.themed) return;
    const original = proto.text;
    proto.text = function (x, y, text, style) {
        return original.call(this, x, y, text, {fontFamily: FONTS.ui, fontStyle: '700', ...style});
    };
    proto.text.themed = true;
}

export function prefersReducedMotion() {
    return typeof window !== 'undefined' && !!window.matchMedia?.('(prefers-reduced-motion: reduce)').matches;
}

/** Resolves once the three font families are usable, or after `timeoutMs`. Never rejects: fallbacks are fine. */
export function loadFonts(timeoutMs = 3000) {
    const fonts = document.fonts;
    if (!fonts?.load) return Promise.resolve();
    const all = Promise.all([
        fonts.load('400 32px "Bungee"'),
        fonts.load('800 32px "Nunito Sans"'),
        fonts.load('700 32px "Space Mono"'),
    ]).catch(() => {});
    return Promise.race([all, new Promise(resolve => setTimeout(resolve, timeoutMs))]);
}
