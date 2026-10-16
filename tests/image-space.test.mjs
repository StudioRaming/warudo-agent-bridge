import assert from 'node:assert/strict';
import test from 'node:test';
import { IMAGE_LIMIT, ImageStore, imageRectToWindow, imageToWindow, windowToImage } from '../src/image-space.mjs';

// A 2560x1440 window cropped to [800, 300, 800, 600] and shown at 1:1 would be 800x600; fitted to 400x300 it is scale 0.5.
const crop = { window: [2560, 1440], crop: [800, 300, 800, 600], image: [400, 300], viewport: [0, 0, 400, 300], scale: 0.5, clickable: true };
// A before/after sheet: a 20 px header and the AFTER panel on the right, same crop at scale 0.5.
const sheet = { window: [2560, 1440], crop: [800, 300, 800, 600], image: [808, 320], viewport: [408, 20, 400, 300], scale: 0.5, clickable: true };

test('image pixels become window pixels: crop offset plus scale', () => {
  assert.deepEqual(imageToWindow(crop, 0, 0), [800, 300]);
  assert.deepEqual(imageToWindow(crop, 400, 300), [1600, 900]);
  assert.deepEqual(imageToWindow(crop, 100, 50), [1000, 400]);
  assert.deepEqual(imageToWindow(crop, 100.25, 50.5), [1000.5, 401]);
});

test('a full-window image at scale 1.5625 (2560 shown at 1638) converts without the caller multiplying', () => {
  const full = { window: [2560, 1440], crop: [0, 0, 2560, 1440], image: [1600, 900], viewport: [0, 0, 1600, 900], scale: 0.625, clickable: true };
  assert.deepEqual(imageToWindow(full, 800, 450), [1280, 720]);
  assert.deepEqual(imageToWindow(full, 1600, 900), [2560, 1440]);
});

test('only the viewport of a composite image holds window pixels', () => {
  assert.deepEqual(imageToWindow(sheet, 408, 20), [800, 300]);
  assert.deepEqual(imageToWindow(sheet, 508, 70), [1000, 400]);
  assert.throws(() => imageToWindow(sheet, 100, 100), { code: 'IMAGE_POINT_OUTSIDE', message: /outside the part that shows the window/ });
  assert.throws(() => imageToWindow(sheet, 500, 5), { code: 'IMAGE_POINT_OUTSIDE' }, 'the header strip is not the window');
});

test('points outside the image, non-finite points and non-clickable images are refused', () => {
  assert.throws(() => imageToWindow(crop, -1, 10), { code: 'IMAGE_POINT_OUTSIDE', message: /outside the 400x300 image/ });
  assert.throws(() => imageToWindow(crop, 401, 10), { code: 'IMAGE_POINT_OUTSIDE' });
  assert.throws(() => imageToWindow(crop, 10, 301), { code: 'IMAGE_POINT_OUTSIDE' });
  assert.throws(() => imageToWindow(crop, Number.NaN, 10), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => imageToWindow(crop, '5', 10), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => imageToWindow({ ...crop, clickable: false }, 10, 10), { code: 'IMAGE_NOT_CLICKABLE' });
  assert.throws(() => imageToWindow({ crop: [0, 0, 0, 0] }, 1, 1), /Malformed/);
});

test('windowToImage is the inverse inside the crop', () => {
  for (const [x, y] of [[0, 0], [123.5, 77], [400, 300]]) {
    const [wx, wy] = imageToWindow(sheet, x + 408, y + 20);
    assert.deepEqual(windowToImage(sheet, wx, wy), [x + 408, y + 20]);
  }
});

test('a region is converted by its two corners', () => {
  assert.deepEqual(imageRectToWindow(crop, [100, 50, 40, 20]), [1000, 400, 80, 40]);
  assert.throws(() => imageRectToWindow(crop, [100, 50, 0, 20]), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => imageRectToWindow(crop, [380, 50, 40, 20]), { code: 'IMAGE_POINT_OUTSIDE' });
});

test('the store keeps the last 32 images, the default is the latest, expired and unknown ids are refused', () => {
  const store = new ImageStore();
  assert.equal(IMAGE_LIMIT, 32);
  assert.throws(() => store.get(), { code: 'IMAGE_UNKNOWN', message: /No image yet/ });
  const ids = [];
  for (let n = 0; n < 40; n += 1) ids.push(store.add({ ...crop, crop: [n, 0, 800, 600] }));
  assert.equal(new Set(ids).size, 40, 'ids are unique');
  assert.equal(store.get().imageId, ids[39], 'the default is the latest');
  assert.equal(store.get(ids[8]).mapping.crop[0], 8);
  assert.throws(() => store.get(ids[7]), { code: 'IMAGE_UNKNOWN', message: /expired/ });
  assert.throws(() => store.get('img-0000-1'), { code: 'IMAGE_UNKNOWN' });
});

test('convert turns x/y, path, points, regions and region into window pixels and drops imageId', () => {
  const store = new ImageStore();
  const first = store.add(crop);
  store.add(sheet);
  // Explicit imageId wins over the latest.
  const click = store.convert({ space: 'image', imageId: first, x: 100, y: 50, button: 'right' });
  assert.deepEqual(click.args, { space: 'pixels', x: 1000, y: 400, button: 'right' });
  assert.equal(click.imageId, first);
  const drag = store.convert({ space: 'image', imageId: first, path: [[0, 0], [400, 300]], frames: 4 });
  assert.deepEqual(drag.args.path, [[800, 300], [1600, 900]]);
  const probe = store.convert({ space: 'image', imageId: first, points: [[100, 50]], regions: [[100, 50, 40, 20]] });
  assert.deepEqual([probe.args.points, probe.args.regions], [[[1000, 400]], [[1000, 400, 80, 40]]]);
  const zoom = store.convert({ space: 'image', imageId: first, region: [0, 0, 200, 150] });
  assert.deepEqual(zoom.args.region, [800, 300, 400, 300]);
  // No imageId: the latest image (the sheet).
  assert.deepEqual(store.convert({ space: 'image', x: 508, y: 70 }).args, { space: 'pixels', x: 1000, y: 400 });
});

test('convert leaves window-pixel arguments alone and refuses a stray imageId', () => {
  const store = new ImageStore();
  const args = { x: 5, y: 6, space: 'normalized' };
  assert.equal(store.convert(args).args, args);
  assert.deepEqual(store.convert({ x: 5, y: 6 }).args, { x: 5, y: 6 });
  assert.throws(() => store.convert({ x: 5, y: 6, imageId: 'img-1' }), { code: 'INVALID_ARGUMENT' });
  assert.throws(() => store.convert({ space: 'image', x: 5, y: 6 }), { code: 'IMAGE_UNKNOWN' });
  store.add(crop);
  assert.throws(() => store.convert({ space: 'image', x: 5 }), { code: 'INVALID_ARGUMENT' });
});
