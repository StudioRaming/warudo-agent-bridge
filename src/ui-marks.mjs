import { BridgeError } from './transport.mjs';

// warudo_ui_capture numbers the controls it boxes. warudo_ui_click mark=N clicks number N of the latest such capture, but only
// while the screen still holds the same controls: the legend remembers the filters and the id list, and the click lists them
// again first. Pure functions, covered by tests/ui-marks.test.mjs.

const invalid = (message) => new BridgeError('INVALID_ARGUMENT', message);

// What the MCP server keeps from a ui_capture reply. null when the reply has no legend (an older runtime).
export function legendFrom(data) {
  if (!data || !Array.isArray(data.elements) || !data.screen || !Array.isArray(data.crop)) return null;
  return {
    screen: { width: data.screen.width, height: data.screen.height },
    crop: [...data.crop],
    query: typeof data.query === 'string' ? data.query : '',
    interactableOnly: data.interactableOnly !== false,
    ids: data.elements.map((element) => element.id),
    imageId: data.imageId,
  };
}

// The ui.list call that reproduces the legend: same filters, same region, as many rows as the legend has.
export function listParamsFor(legend) {
  return { query: legend.query, interactableOnly: legend.interactableOnly, region: legend.crop, limit: Math.max(1, legend.ids.length) };
}

// The ui.list row for a mark, or an error saying why the number cannot be trusted any more.
export function resolveMark(legend, mark, listed) {
  if (!Number.isInteger(mark) || mark < 1) throw invalid('mark must be an integer >= 1 (the number in the latest warudo_ui_capture image).');
  if (!legend) throw new BridgeError('MARK_UNKNOWN', 'No warudo_ui_capture yet in this session, so there is no mark to resolve. Take one first, or click by near/label/id.');
  if (mark > legend.ids.length) throw new BridgeError('MARK_UNKNOWN', `mark ${mark} is not in the latest warudo_ui_capture, which numbered ${legend.ids.length} control(s).`);
  const stale = (why) => new BridgeError('MARK_STALE', `The screen changed since the latest warudo_ui_capture (${why}). Take a new warudo_ui_capture and use its numbers.`);
  const screen = listed && listed.screen;
  if (!screen || screen.width !== legend.screen.width || screen.height !== legend.screen.height) {
    throw stale(`window size ${legend.screen.width}x${legend.screen.height} is now ${screen ? `${screen.width}x${screen.height}` : 'unknown'}`);
  }
  const now = Array.isArray(listed.items) ? listed.items.map((item) => item.id) : [];
  const same = now.length === legend.ids.length && now.every((id, index) => id === legend.ids[index]);
  if (!same) {
    const gone = legend.ids.filter((id) => !now.includes(id)).length;
    const added = now.filter((id) => !legend.ids.includes(id)).length;
    throw stale(`${gone} control(s) gone, ${added} new, ${gone + added === 0 ? 'order changed' : 'ids differ'}`);
  }
  return listed.items[mark - 1];
}
