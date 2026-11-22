import {hubErrorMessage} from '../../core/gameHub.js';
import {createTextButton} from '../../core/ui/Button.js';
import {COLORS, textStyle} from '../../core/theme.js';

/**
 * The button that moves the night on (past a game's results, the scoreboard, or the finale). Only the host and the
 * leader (the first player to join) may press it; the server checks. Returns {button, status}.
 */
export function addContinueButton(scene, x, y, label, {width = 300, height = 64, fontSize = '28px', variant = 'primary'} = {}) {
    const status = scene.add.text(x, y + height / 2 + 22, '', textStyle('body', {fontSize: '18px', color: COLORS.danger})).setOrigin(0.5);
    const button = createTextButton(scene, x, y, label, () => {
        button.setEnabled(false);
        status.setText('');
        scene.session.invoke('ContinueNight').catch(err => {
            button.setEnabled(true);
            status.setText(hubErrorMessage(err));
        });
    }, {width, height, fontSize, variant});
    return {button, status};
}
