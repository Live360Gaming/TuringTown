// The goodbye on the Finale: "Thanks for playing!" and one more line, picked by the server so every screen agrees.
import {narrationEntry} from '../../narration/registry.js';

export const THANKS = 'goodbye_thanks';

/** The narration keys to say, in order. */
export const goodbyeKeys = goodbye => goodbye ? [THANKS, goodbye] : [THANKS];

export const goodbyeText = goodbye => goodbyeKeys(goodbye).map(key => narrationEntry(key).transcript).join('\n');
