import crypto from 'node:crypto';
import { BridgeError } from './transport.mjs';

// "Click where you see it": every image tool returns an imageId and a mapping (the window crop the PNG shows, the PNG size and
// the part of the PNG that holds that crop). The MCP server remembers the last IMAGE_LIMIT mappings. A pointer or capture tool
// called with space "image" gets its x/y (path, points, region) in pixels of one of those PNGs; imageToWindow turns them into
// window pixels. Pure functions, covered by tests/image-space.test.mjs.
//
// mapping = { window: [w, h], crop: [x, y, w, h], image: [w, h], viewport: [x, y, w, h], scale, clickable }
//   crop      the window rectangle the image shows (window pixels, top-left origin)
//   viewport  where that crop sits in the PNG (a plain capture: the whole PNG; a before/after sheet: the AFTER panel)

export const IMAGE_LIMIT = 32;
const round3 = (value) => Math.round(value * 1000) / 1000;
const invalid = (message) => new BridgeError('INVALID_ARGUMENT', message);

function checkMapping(mapping) {
  const ok = mapping && Array.isArray(mapping.crop) && mapping.crop.length === 4 && Array.isArray(mapping.image) && mapping.image.length === 2 &&
    Array.isArray(mapping.viewport) && mapping.viewport.length === 4 && [...mapping.crop, ...mapping.image, ...mapping.viewport].every(Number.isFinite) &&
    mapping.crop[2] > 0 && mapping.crop[3] > 0 && mapping.viewport[2] > 0 && mapping.viewport[3] > 0;
  if (!ok) throw new Error('Malformed image mapping.');
  return mapping;
}

// One point of a PNG to window pixels. Points outside the PNG, outside the part that shows the window, or on an image that is
// not a view of the window (a contact sheet) are refused, so a misread coordinate never turns into a click somewhere else.
export function imageToWindow(mapping, x, y, name = 'x/y') {
  checkMapping(mapping);
  if (mapping.clickable === false) {
    throw new BridgeError('IMAGE_NOT_CLICKABLE', 'This image is a contact sheet or composite, not a view of the window. Use an image from screen_capture, ui_capture or screen_diff, or pass window pixels.');
  }
  if (typeof x !== 'number' || typeof y !== 'number' || !Number.isFinite(x) || !Number.isFinite(y)) throw invalid(`${name} must be finite numbers.`);
  const [imageWidth, imageHeight] = mapping.image;
  if (x < 0 || y < 0 || x > imageWidth || y > imageHeight) {
    throw new BridgeError('IMAGE_POINT_OUTSIDE', `${name} (${x}, ${y}) is outside the ${imageWidth}x${imageHeight} image.`);
  }
  const [viewX, viewY, viewWidth, viewHeight] = mapping.viewport;
  if (x < viewX || y < viewY || x > viewX + viewWidth || y > viewY + viewHeight) {
    throw new BridgeError('IMAGE_POINT_OUTSIDE', `${name} (${x}, ${y}) is inside the image but outside the part that shows the window (x ${viewX}..${viewX + viewWidth}, y ${viewY}..${viewY + viewHeight}).`);
  }
  const [cropX, cropY, cropWidth, cropHeight] = mapping.crop;
  return [round3(cropX + ((x - viewX) * cropWidth) / viewWidth), round3(cropY + ((y - viewY) * cropHeight) / viewHeight)];
}

// A rectangle [x, y, w, h] of a PNG to window pixels (both corners go through imageToWindow).
export function imageRectToWindow(mapping, rect, name = 'region') {
  if (!Array.isArray(rect) || rect.length !== 4) throw invalid(`${name} must be [x, y, width, height].`);
  const [x, y, width, height] = rect;
  if (typeof width !== 'number' || typeof height !== 'number' || width <= 0 || height <= 0) throw invalid(`${name} width and height must be positive.`);
  const [x0, y0] = imageToWindow(mapping, x, y, `${name} corner`);
  const [x1, y1] = imageToWindow(mapping, x + width, y + height, `${name} far corner`);
  return [x0, y0, round3(x1 - x0), round3(y1 - y0)];
}

// The window pixel where a PNG pixel lands, the other way round (for tests and for describing marks).
export function windowToImage(mapping, x, y) {
  checkMapping(mapping);
  const [cropX, cropY, cropWidth, cropHeight] = mapping.crop;
  const [viewX, viewY, viewWidth, viewHeight] = mapping.viewport;
  return [round3(viewX + ((x - cropX) * viewWidth) / cropWidth), round3(viewY + ((y - cropY) * viewHeight) / cropHeight)];
}

export class ImageStore {
  constructor(limit = IMAGE_LIMIT) {
    this.limit = limit;
    this.salt = crypto.randomBytes(2).toString('hex');
    this.counter = 0;
    this.items = new Map();
  }

  add(mapping) {
    checkMapping(mapping);
    this.counter += 1;
    const id = `img-${this.salt}-${this.counter}`;
    this.items.set(id, mapping);
    while (this.items.size > this.limit) this.items.delete(this.items.keys().next().value);
    return id;
  }

  latestId() {
    let last;
    for (const id of this.items.keys()) last = id;
    return last;
  }

  // imageId undefined means the latest image of this server.
  get(imageId) {
    const id = imageId ?? this.latestId();
    if (id === undefined) {
      throw new BridgeError('IMAGE_UNKNOWN', 'No image yet: take one with warudo_screen_capture, warudo_ui_capture or a gesture with capture=true, then pass space="image".');
    }
    const mapping = this.items.get(id);
    if (!mapping) {
      throw new BridgeError('IMAGE_UNKNOWN', `Unknown or expired imageId "${id}". This server keeps the last ${this.limit} images; take a new capture or omit imageId to use the latest.`, { latest: this.latestId() });
    }
    return { imageId: id, mapping };
  }

  // Window-pixel arguments from image-pixel arguments: x/y, path, points, region(s). Returns the new arguments (space "pixels",
  // no imageId) and which image was used. Arguments that are not in image space pass through unchanged.
  convert(args) {
    if (args.space !== 'image') {
      if (args.imageId !== undefined) throw invalid('imageId only applies with space="image".');
      return { args, imageId: undefined };
    }
    const { imageId, mapping } = this.get(args.imageId);
    const out = { ...args, space: 'pixels' };
    delete out.imageId;
    const pair = (value, name) => {
      if (!Array.isArray(value) || value.length !== 2) throw invalid(`${name} must be [x, y].`);
      return imageToWindow(mapping, value[0], value[1], name);
    };
    if (args.x !== undefined || args.y !== undefined) {
      if (args.x === undefined || args.y === undefined) throw invalid('Pass x and y together.');
      [out.x, out.y] = imageToWindow(mapping, args.x, args.y);
    }
    if (args.path !== undefined) {
      if (!Array.isArray(args.path)) throw invalid('path must be an array of [x, y].');
      out.path = args.path.map((point, index) => pair(point, `path[${index}]`));
    }
    if (args.points !== undefined) {
      if (!Array.isArray(args.points)) throw invalid('points must be an array of [x, y].');
      out.points = args.points.map((point, index) => pair(point, `points[${index}]`));
    }
    if (args.regions !== undefined) {
      if (!Array.isArray(args.regions)) throw invalid('regions must be an array of [x, y, width, height].');
      out.regions = args.regions.map((rect, index) => imageRectToWindow(mapping, rect, `regions[${index}]`));
    }
    if (args.region !== undefined) out.region = imageRectToWindow(mapping, args.region);
    return { args: out, imageId };
  }
}
