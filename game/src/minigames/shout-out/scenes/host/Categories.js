import {HOST_CENTER_X} from '../../../../constants.js';
import {HostStage} from '../../../../core/stage/HostStage.js';
import {COLORS, HEX, textStyle} from '../../../../core/theme.js';

const EDGES = [HEX.cyan, HEX.pink, HEX.gold, HEX.green];

// The category vote: the choices on the TV, the votes coming in.
export class ShoutOutHostCategories extends HostStage {
    constructor() {
        super('ShoutOutHostCategories');
    }

    build() {
        const {categories = [], maxPicks = 2} = this.session.state.data ?? {};
        this.setTitle('Pick your categories');
        this.setInstruction(`Choose up to ${maxPicks} on your phone`);

        const w = 360, h = 110, gapX = 30, gapY = 30, cols = 3;
        categories.forEach((category, i) => {
            const row = Math.floor(i / cols);
            const inRow = Math.min(cols, categories.length - row * cols);
            const x = HOST_CENTER_X + ((i % cols) - (inRow - 1) / 2) * (w + gapX);
            const y = 260 + row * (h + gapY);
            this.add.graphics()
                .fillStyle(HEX.bgDeep, 0.9).fillRoundedRect(x - w / 2, y - h / 2, w, h, 20)
                .lineStyle(4, EDGES[i % EDGES.length], 1).strokeRoundedRect(x - w / 2, y - h / 2, w, h, 20);
            this.add.text(x, y, category.name, textStyle('name', {fontSize: '30px', align: 'center', wordWrap: {width: w - 30}}))
                .setOrigin(0.5);
        });

        this.add.text(HOST_CENTER_X, 560, 'The two most picked make the cut, plus one wildcard.',
            textStyle('bodyOnArt', {fontSize: '22px', color: COLORS.cyan})).setOrigin(0.5);

        this.listen(this.session, 'progress', () => this.showProgress());
        this.showProgress();
    }

    showProgress() {
        const {done, total} = this.session.progress;
        this.setProgress(done, total);
    }
}
