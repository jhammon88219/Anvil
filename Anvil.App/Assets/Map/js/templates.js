// ============================================================================================
// templates.js — clones the page's UI components out of the <template>s in map.html / preview.html.
//
//     map.html   <template id="tpl-site-key"> …markup… </template>      ← DEFINED before runtime
//                          │
//        JS  ──────────────┴─►  Templates.clone('tpl-site-key')       ← only INSTANTIATED at runtime
//
// THE RULE (2026-10-12): every UI component the page draws is defined BEFORE runtime — its markup in
// a <template>, its look in styles/ — and never during. A module may clone a template, fill text /
// data attributes / per-instance geometry, and toggle classes. It may not build markup in a string,
// set a static look inline, or inject a <style>. See Assets/Map/README.md ("Where the UI is defined").
//
// SVG parts: a <template> holding a bare <circle> would parse it as an HTML element, so an SVG
// fragment is wrapped in an <svg> inside its template and cloned with a selector:
//     Templates.clone('tpl-site-leader', '.radar-site-leader-origin')
//
// ⚠️ A missing template THROWS, loudly, by name. It is a page/markup mismatch that no fallback can
// paper over, and the pageError reporter in map.html carries the message to the C# log.
// ============================================================================================

/**
 * A fresh deep copy of a template's root element (or of the element `part` selects inside it).
 * @param {string} id    the <template>'s id ('tpl-site-key').
 * @param {string} [part] a selector inside the template, for SVG fragments wrapped in an <svg>.
 */
export function clone(id, part) {
    const t = document.getElementById(id);
    if (!t || !t.content) throw new Error('templates.js: no <template id="' + id + '"> on this page');
    const el = part ? t.content.querySelector(part) : t.content.firstElementChild;
    if (!el) throw new Error('templates.js: <template id="' + id + '"> has no ' + (part || 'root element'));
    return el.cloneNode(true);
}

/**
 * Put `value` as the TEXT of the element `sel` selects inside `root` (never HTML — feed text is data),
 * and hide that element when there is nothing to say. Returns the element.
 */
export function text(root, sel, value) {
    const el = root.querySelector(sel);
    if (!el) throw new Error('templates.js: no ' + sel + ' in this component');
    const s = value === null || value === undefined ? '' : String(value);
    el.textContent = s;
    el.hidden = !s;
    return el;
}
