// model-field.js — ForeCast's forecast-model field on the map (the Short Range section: HRRR future radar, updraft
// helicity tracks). ONE WebGL custom layer per pane, drawing one FRAME at a time. The host builds everything
// (HrrrService + ModelFieldFrames) and names the frame with window.setModelField(descriptor); this module only
// fetches, uploads and draws. map.js's window.*ModelField* shims delegate here; reAddAll calls reAdd(map).
//
//     the model's own grid (Lambert, 1799 × 1059 for the HRRR)           on the map (Web Mercator)
//     ┌──────────────────────────────┐                                 ╭──────────────────────────────╮
//     │ one BYTE per cell = a code   │  the MESH (128 × 80 lon/lat     ╱  ▒▒▓▓░░        ░░▒▒            ╲
//     │ 0 = nothing; 1…255 → value   │  points from the host) bends   ╱     ▓██▓▒░    ░▒▓▓▒░             ╲
//     │ (code − offset) / scale      │  the grid onto the map; the   ╱         ▒▓▒░       ░░                ╲
//     │ row 0 = SOUTH                │  shader stretches the frame   └──────────────────────────────────────┘
//     └──────────────────────────────┘  between them (≤ 59 m off)      colour = LUT[code] (a 256 × 1 texture)
//
//   descriptor = { url, mesh, nx, ny, scale, offset, ramp: 'reflectivity' | 'uh', stops?: [{ v, color }] }
//
//   COMPARE WITH RADAR (ShortRangeViewModel.IsComparing): setPanes limits the field to some panes (pane 1); map.js
//   hides the RADAR data layer in those same panes, so one pane is radar and the other the model, never both.
//
//     ┌──────── pane 0 ────────┐┌──────── pane 1 ────────┐
//     │  live radar (level2-)   ││  model-field only       │   (rings / ruler still draw in both)
//     └─────────────────────────┘└─────────────────────────┘
//
//   PLAYING (the bottom console's model mode, ModelPlaybackViewModel): the host sends one descriptor per step; the
//   compressed bytes of recent frames stay cached here, each step inflates one, and the frame on screen stays up until
//   the next is ready — no blank between frames.
//
// ⚠️ COLOURS ARE DATA, never themed. Reflectivity uses RADAR'S OWN table (radar-ramps.js REFLECTIVITY_RAMP + rampColor,
// imported read-only — the sealed engine is not edited), so a forecast 50 dBZ is the same red as live radar's. Other
// fields bring their stops in the descriptor (Models/Forecast/ModelRamps.cs is the one copy).
// ⚠️ NEAREST sampling of our OWN textures in our own shader: crisp 3 km cells, and code 0 is discarded — the
// "transparent raster + raster-resampling:nearest = black square under ANGLE" gotcha is MapLibre's raster layer, not this.
// ⚠️ A frame file is gzip'd but may arrive either way (the virtual host may or may not label it): the gzip magic decides.
// ⚠️ UNPACK_ALIGNMENT 1: a 1799-wide row of bytes is not 4-aligned.
// ⚠️ ENCODING MIRRORED with Models/Forecast/ModelFieldEncoding.cs: only the rule — the numbers ride in the descriptor.

import { firstBoundaryLayerId } from './layers.js';
import { REFLECTIVITY_RAMP, rampColor } from './radar-ramps.js';

const LAYER_ID = 'model-field';

let allowed = null;       // which panes draw it: null = every pane, else (map) => bool (Compare with radar: pane 1 only)
let want = null;          // the descriptor the host last asked for (null = nothing)
let shown = null;         // the descriptor whose frame + mesh are loaded: { desc, codes, mesh, lut, key }
let opacity = 1;
const meshes = {};        // mesh url → Promise<{ positions, uvs, indices, count }>
const views = new WeakMap(); // map → { program, …GL objects, uploaded keys } — GL objects belong to ONE context

// ── Loading ───────────────────────────────────────────────────────────────────────────────────────

// The console PLAYS a run, so the same ~70 KB frames come round again and again: keep the COMPRESSED bytes of the last
// KEEP_FRAMES (≈ 7 MB) and inflate per step — never the 1.9 MB inflated ones (×100 frames × panes would be ~200 MB).
// A frame file never changes once written (a run's landed hour is final), so a url is a safe key.
const KEEP_FRAMES = 110;
const raw = new Map();      // url → ArrayBuffer (as fetched), oldest first

async function fetchRaw(url) {
    if (raw.has(url)) {
        const hit = raw.get(url);
        raw.delete(url); raw.set(url, hit); // most recent last
        return hit;
    }
    const r = await fetch(url, { cache: 'no-store' });
    if (!r.ok) throw new Error('frame ' + r.status);
    const buf = await r.arrayBuffer();
    raw.set(url, buf);
    while (raw.size > KEEP_FRAMES) raw.delete(raw.keys().next().value);
    return buf;
}

async function loadCodes(url, count) {
    let buf = await fetchRaw(url);
    const head = new Uint8Array(buf, 0, Math.min(2, buf.byteLength));
    if (head[0] === 0x1f && head[1] === 0x8b) {
        buf = await new Response(new Blob([buf]).stream().pipeThrough(new DecompressionStream('gzip'))).arrayBuffer();
    }
    const codes = new Uint8Array(buf);
    if (codes.length !== count) throw new Error('frame has ' + codes.length + ' cells, expected ' + count);
    return codes;
}

function loadMesh(url) {
    if (!meshes[url]) {
        meshes[url] = fetch(url, { cache: 'no-store' }).then(function (r) {
            if (!r.ok) throw new Error('mesh ' + r.status);
            return r.json();
        }).then(buildMesh).catch(function (e) { delete meshes[url]; throw e; });
    }
    return meshes[url];
}

// Mesh points → Mercator positions + the texel each one samples. (c, r) sits on grid point
// (c·(nx−1)/cols, r·(ny−1)/rows); its texture coordinate is that point's texel CENTRE.
function buildMesh(m) {
    const cols = m.cols, rows = m.rows, n = (cols + 1) * (rows + 1);
    const positions = new Float32Array(n * 2), uvs = new Float32Array(n * 2);
    for (let r = 0, k = 0; r <= rows; r++) {
        for (let c = 0; c <= cols; c++, k++) {
            const p = maplibregl.MercatorCoordinate.fromLngLat({ lng: m.lonlat[k * 2], lat: m.lonlat[k * 2 + 1] });
            positions[k * 2] = p.x;
            positions[k * 2 + 1] = p.y;
            uvs[k * 2] = (c * (m.nx - 1) / cols + 0.5) / m.nx;
            uvs[k * 2 + 1] = (r * (m.ny - 1) / rows + 0.5) / m.ny;
        }
    }
    const indices = new Uint16Array(cols * rows * 6);
    for (let r = 0, i = 0; r < rows; r++) {
        for (let c = 0; c < cols; c++) {
            const a = r * (cols + 1) + c, b = a + 1, d = a + cols + 1, e = d + 1;
            indices[i++] = a; indices[i++] = b; indices[i++] = e;
            indices[i++] = a; indices[i++] = e; indices[i++] = d;
        }
    }
    return { positions: positions, uvs: uvs, indices: indices, count: indices.length };
}

// Code → RGBA, 256 entries: code 0 transparent, 1…255 the field's colour at (code − offset) / scale.
function buildLut(desc) {
    const ramp = desc.ramp === 'reflectivity' ? REFLECTIVITY_RAMP : { stops: desc.stops || [], interpolate: false };
    const lut = new Uint8Array(256 * 4);
    if (!ramp.stops.length) return lut;
    for (let code = 1; code < 256; code++) {
        const c = rampColor(ramp, (code - desc.offset) / desc.scale);
        lut[code * 4] = c[0]; lut[code * 4 + 1] = c[1]; lut[code * 4 + 2] = c[2]; lut[code * 4 + 3] = 255;
    }
    return lut;
}

// ── GL ────────────────────────────────────────────────────────────────────────────────────────────

function compile(gl, type, src) {
    const s = gl.createShader(type);
    gl.shaderSource(s, src);
    gl.compileShader(s);
    if (!gl.getShaderParameter(s, gl.COMPILE_STATUS)) throw new Error(gl.getShaderInfoLog(s) || 'shader compile failed');
    return s;
}

function texture(gl) {
    const t = gl.createTexture();
    gl.bindTexture(gl.TEXTURE_2D, t);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    return t;
}

function makeLayer(v) {
    return {
        id: LAYER_ID,
        type: 'custom',
        onAdd: function (map, gl) {
            const vs = compile(gl, gl.VERTEX_SHADER,
                'uniform mat4 u_matrix;' +
                'attribute vec2 a_pos;' +
                'attribute vec2 a_uv;' +
                'varying vec2 v_uv;' +
                'void main(){ v_uv=a_uv; gl_Position=u_matrix*vec4(a_pos,0.0,1.0); }');
            const fs = compile(gl, gl.FRAGMENT_SHADER,
                'precision highp float;' +
                'uniform sampler2D u_data;' +
                'uniform sampler2D u_lut;' +
                'uniform float u_opacity;' +
                'varying vec2 v_uv;' +
                'void main(){' +
                '  float code=floor(texture2D(u_data,v_uv).r*255.0+0.5);' +
                '  vec4 c=texture2D(u_lut,vec2((code+0.5)/256.0,0.5));' +
                '  if(c.a==0.0) discard;' +
                '  gl_FragColor=vec4(c.rgb,c.a*u_opacity);' +
                '}');
            v.program = gl.createProgram();
            gl.attachShader(v.program, vs);
            gl.attachShader(v.program, fs);
            gl.linkProgram(v.program);
            if (!gl.getProgramParameter(v.program, gl.LINK_STATUS)) throw new Error(gl.getProgramInfoLog(v.program) || 'program link failed');
            v.aPos = gl.getAttribLocation(v.program, 'a_pos');
            v.aUv = gl.getAttribLocation(v.program, 'a_uv');
            v.uMatrix = gl.getUniformLocation(v.program, 'u_matrix');
            v.uOpacity = gl.getUniformLocation(v.program, 'u_opacity');
            v.uData = gl.getUniformLocation(v.program, 'u_data');
            v.uLut = gl.getUniformLocation(v.program, 'u_lut');
            v.posBuf = gl.createBuffer();
            v.uvBuf = gl.createBuffer();
            v.idxBuf = gl.createBuffer();
            v.dataTex = texture(gl);
            v.lutTex = texture(gl);
            v.frameKey = v.meshKey = v.lutKey = null;
        },
        render: function (gl, args) {
            const s = shown;
            if (!v.program || !s) return;
            try {
                if (v.meshKey !== s.desc.mesh) {
                    gl.bindBuffer(gl.ARRAY_BUFFER, v.posBuf);
                    gl.bufferData(gl.ARRAY_BUFFER, s.mesh.positions, gl.STATIC_DRAW);
                    gl.bindBuffer(gl.ARRAY_BUFFER, v.uvBuf);
                    gl.bufferData(gl.ARRAY_BUFFER, s.mesh.uvs, gl.STATIC_DRAW);
                    gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, v.idxBuf);
                    gl.bufferData(gl.ELEMENT_ARRAY_BUFFER, s.mesh.indices, gl.STATIC_DRAW);
                    v.meshKey = s.desc.mesh;
                }
                gl.pixelStorei(gl.UNPACK_ALIGNMENT, 1);
                if (v.frameKey !== s.desc.url) {
                    gl.bindTexture(gl.TEXTURE_2D, v.dataTex);
                    gl.texImage2D(gl.TEXTURE_2D, 0, gl.LUMINANCE, s.desc.nx, s.desc.ny, 0, gl.LUMINANCE, gl.UNSIGNED_BYTE, s.codes);
                    v.frameKey = s.desc.url;
                }
                if (v.lutKey !== s.key) {
                    gl.bindTexture(gl.TEXTURE_2D, v.lutTex);
                    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 256, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, s.lut);
                    v.lutKey = s.key;
                }
                gl.pixelStorei(gl.UNPACK_ALIGNMENT, 4);

                const matrix = (args && args.defaultProjectionData && args.defaultProjectionData.mainMatrix)
                    || (args && args.modelViewProjectionMatrix) || args;
                gl.useProgram(v.program);
                gl.uniformMatrix4fv(v.uMatrix, false, matrix);
                gl.uniform1f(v.uOpacity, opacity);
                gl.activeTexture(gl.TEXTURE0);
                gl.bindTexture(gl.TEXTURE_2D, v.dataTex);
                gl.uniform1i(v.uData, 0);
                gl.activeTexture(gl.TEXTURE1);
                gl.bindTexture(gl.TEXTURE_2D, v.lutTex);
                gl.uniform1i(v.uLut, 1);
                gl.bindBuffer(gl.ARRAY_BUFFER, v.posBuf);
                gl.enableVertexAttribArray(v.aPos);
                gl.vertexAttribPointer(v.aPos, 2, gl.FLOAT, false, 0, 0);
                gl.bindBuffer(gl.ARRAY_BUFFER, v.uvBuf);
                gl.enableVertexAttribArray(v.aUv);
                gl.vertexAttribPointer(v.aUv, 2, gl.FLOAT, false, 0, 0);
                gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER, v.idxBuf);
                gl.enable(gl.BLEND);
                gl.blendFunc(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA);
                gl.drawElements(gl.TRIANGLES, s.mesh.count, gl.UNSIGNED_SHORT, 0);
                gl.disableVertexAttribArray(v.aPos);
                gl.disableVertexAttribArray(v.aUv);
                gl.bindBuffer(gl.ARRAY_BUFFER, null);
                gl.activeTexture(gl.TEXTURE0);
            } catch (e) {
                console.error('model-field render failed: ' + (e && e.message ? e.message : e));
            }
        },
        onRemove: function (map, gl) {
            [v.posBuf, v.uvBuf, v.idxBuf].forEach(function (b) { if (b) gl.deleteBuffer(b); });
            [v.dataTex, v.lutTex].forEach(function (t) { if (t) gl.deleteTexture(t); });
            if (v.program) gl.deleteProgram(v.program);
            v.program = v.posBuf = v.uvBuf = v.idxBuf = v.dataTex = v.lutTex = null;
            v.frameKey = v.meshKey = v.lutKey = null;
        },
    };
}

function viewOf(map) {
    let v = views.get(map);
    if (!v) { v = {}; views.set(map, v); }
    return v;
}

function ensureLayer(map) {
    if (!shown || (allowed && !allowed(map))) { removeLayer(map); return; }
    if (map.getLayer(LAYER_ID)) { map.triggerRepaint(); return; }
    map.addLayer(makeLayer(viewOf(map)), firstBoundaryLayerId(map));
}

function removeLayer(map) {
    if (map.getLayer(LAYER_ID)) map.removeLayer(LAYER_ID);
}

// ── Host commands ─────────────────────────────────────────────────────────────────────────────────

// ⚠️ Called once PER PANE with the same descriptor (map.js fans out). The first call starts the load; the others
// find it pending or done. The frame on screen stays until the new one is loaded — no blank between frames.
export function setField(map, json) {
    const desc = typeof json === 'string' ? JSON.parse(json) : json;
    const key = desc.url + '|' + desc.mesh + '|' + desc.ramp + '|' + desc.scale + '|' + desc.offset;
    if (!want || want.key !== key) {
        want = { key: key, desc: desc, maps: new Set(), done: false };
        const asked = want;
        Promise.all([loadCodes(desc.url, desc.nx * desc.ny), loadMesh(desc.mesh)]).then(function (got) {
            if (want !== asked) return;
            const lutKey = desc.ramp + '|' + desc.scale + '|' + desc.offset;
            shown = { desc: desc, codes: got[0], mesh: got[1], key: lutKey,
                lut: shown && shown.key === lutKey ? shown.lut : buildLut(desc) };
            asked.done = true;
            asked.maps.forEach(ensureLayer);
        }).catch(function (e) { console.error('model-field load failed: ' + e); });
    }
    want.maps.add(map);
    if (want.done || shown) ensureLayer(map);
}

export function setOpacity(map, o) {
    opacity = Math.max(0, Math.min(1, +o || 0));
    if (map.getLayer(LAYER_ID)) map.triggerRepaint();
}

export function clear(map) {
    want = null;
    shown = null;
    removeLayer(map);
}

// Which panes draw the field (map.js passes a test over its own pane indices; null = all). Then call refresh(map) on
// every pane so each adds or drops its layer.
export function setPanes(test) {
    allowed = typeof test === 'function' ? test : null;
}

export function refresh(map) {
    ensureLayer(map);
}

export function reAdd(map) {
    if (want) want.maps.add(map);
    if (shown) ensureLayer(map);
}
