import assert from 'node:assert/strict';
import {test} from 'node:test';
import {speechOccurrence} from '../src/core/narration/scopes.js';
import {shouldReload} from '../src/core/version.js';

test('an old page reloads once for a new build, and never loops', () => {
    assert.equal(shouldReload('b2', 'b1', null), true);
    assert.equal(shouldReload('b2', 'b1', 'b2'), false);
    assert.equal(shouldReload('b3', 'b1', 'b2'), true);
    assert.equal(shouldReload('b1', 'b1', null), false);
    assert.equal(shouldReload(null, 'b1', null), false);
    assert.equal(shouldReload('b2', null, null), false);
});

test('a line said over a screen is one occurrence per round', () => {
    assert.equal(speechOccurrence('tap', 1), speechOccurrence('tap', 1));
    assert.notEqual(speechOccurrence('final', 3), speechOccurrence('final', 4));
});
