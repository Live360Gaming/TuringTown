import {HOST_CENTER_X, HOST_HEIGHT, HOST_WIDTH} from '../../../../constants.js';
import {createAvatar} from '../../../../core/characters.js';
import {hubErrorMessage} from '../../../../core/gameHub.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {textStyle} from '../../../../core/theme.js';
import {TutorialPlayer} from '../../../../core/tutorial/TutorialPlayer.js';
import {createTextButton} from '../../../../core/ui/Button.js';
import {narrationKey, TRANSITIONS} from '../../data/transitions.js';

// The announcer explains the next part. The TV plays the line, then tells the server to move on.
export class TuringTownHostTransition extends HostStage {
    constructor() {
        super('TuringTownHostTransition');
    }

    build() {
        this.timerText.setVisible(false);
        this.beat = null;
        this.player = null;
        this.extras = [];

        createTextButton(this, HOST_WIDTH - 130, HOST_HEIGHT - 50, 'Skip', () => this.player?.finish(), {width: 200, height: 56, fontSize: '24px'});

        // Two transitions can follow each other without a scene change, so each new beat is drawn from the state.
        this.listen(this.session, 'state', () => this.showBeat());
        this.events.once('shutdown', () => this.player?.destroy());
        this.showBeat();
    }

    showBeat() {
        const {data, stage} = this.session.state;
        if (stage !== 'Transition' || data?.beat === this.beat) return;
        const step = TRANSITIONS[data.beat];
        if (!step) return;
        this.beat = data.beat;

        this.player?.destroy();
        this.player?.layer.destroy();
        this.player?.title.destroy();
        this.player?.caption.destroy();
        this.player?.dots.forEach(dot => dot.destroy());
        this.extras.forEach(item => item.destroy());
        this.extras = [];

        this.setTitle(step.title);
        this.setInstruction(data.hostLine ?? '');

        const hasCard = !!data.hotSeat;
        const beat = data.beat;
        this.player = new TutorialPlayer(this, [{
            id: beat, type: 'animation', animation: hasCard ? 'blank' : step.animation, narration: narrationKey(beat),
            caption: step.caption, durationMs: step.durationMs,
        }], {
            onDone: () => this.session.invoke('TransitionDone', beat).catch(err => console.warn(hubErrorMessage(err))),
        });
        this.player.dots.forEach(dot => dot.setVisible(false));
        this.player.start(0);

        if (hasCard) this.showHotSeat(data);
    }

    showHotSeat({hotSeat, topic, answer, beat}) {
        this.extras.push(createAvatar(this, HOST_CENTER_X, 240, 170, hotSeat));
        this.extras.push(this.add.text(HOST_CENTER_X, 350, hotSeat.alias, textStyle('title', {fontSize: '34px', color: hotSeat.color})).setOrigin(0.5));
        if (beat !== 'answer') return;
        this.extras.push(this.add.text(HOST_CENTER_X, 440, `${topic}\n"${answer ?? 'said nothing'}"`, textStyle('instruction', {
            fontSize: '32px', align: 'center', wordWrap: {width: 1100},
        })).setOrigin(0.5));
    }
}
