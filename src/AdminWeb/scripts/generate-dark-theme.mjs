// Generates src/theme-dark.css from the light stylesheets so the dark theme cannot drift from them.
// Rule: light backgrounds and borders become dark, dark text becomes light; saturated or already-dark
// fills (buttons, the navy sidebar, the command palette) and shadows are left as they are.
// Usage: node scripts/generate-dark-theme.mjs          (write)
//        node scripts/generate-dark-theme.mjs --check  (fail if the committed file is stale)
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

const root = join(dirname(fileURLToPath(import.meta.url)), '..', 'src');
const sources = ['management.css', 'styles.css', 'admin-experience.css'];
const output = join(root, 'theme-dark.css');
const prefix = ':root[data-theme="dark"]';

// Components that are dark in both themes keep their own colours.
const alreadyDark = /sidebar|\.nav-|brand|command-|realtime-toast button/;

// Design tokens get hand-picked dark values instead of the generic rule.
const tokens = {
  '--surface': '#111a2b', '--line': '#273449', '--muted': '#a3aec2', '--accent-soft': '#1b2a4a',
  '--danger': '#fd8b80', '--success': '#4ade9b', '--warning': '#fbbf4d',
  '--tm-ink': '#e8eef6', '--tm-subtle': '#a3aec2', '--tm-border': 'rgba(148, 163, 184, .2)',
  '--tm-panel': 'rgba(17, 26, 43, .9)'
};
const extraRoot = 'color-scheme: dark; --panel-bg: #111a2b;';
// Controls the light stylesheets leave to browser defaults (white fields) need explicit dark fills.
const handWritten = `
/* hand-written */
${prefix} :where(input, select, textarea):not([type="checkbox"]):not([type="radio"]) {
  background-color: var(--surface);
  color: var(--tm-ink);
  border-color: var(--line);
}
${prefix} ::placeholder {
  color: var(--muted);
}
`;

function parseColor(text) {
  if (text === 'white') text = '#ffffff';
  if (text === 'black') text = '#000000';
  let m = /^#([0-9a-f]{3,8})$/i.exec(text);
  if (m) {
    let h = m[1];
    if (h.length === 3 || h.length === 4) h = [...h].map(c => c + c).join('');
    const n = parseInt(h.slice(0, 6), 16);
    return { r: n >> 16, g: (n >> 8) & 255, b: n & 255, a: h.length === 8 ? parseInt(h.slice(6), 16) / 255 : 1 };
  }
  m = /^rgba?\(\s*([\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)(?:\s*[,/]\s*([\d.]+%?))?\s*\)$/i.exec(text);
  if (m) {
    const a = m[4] === undefined ? 1 : m[4].endsWith('%') ? parseFloat(m[4]) / 100 : parseFloat(m[4]);
    return { r: +m[1], g: +m[2], b: +m[3], a };
  }
  return null;
}

function toHsl({ r, g, b }) {
  r /= 255; g /= 255; b /= 255;
  const max = Math.max(r, g, b), min = Math.min(r, g, b), l = (max + min) / 2;
  if (max === min) return { h: 0, s: 0, l };
  const d = max - min;
  const s = l > .5 ? d / (2 - max - min) : d / (max + min);
  const h = max === r ? (g - b) / d + (g < b ? 6 : 0) : max === g ? (b - r) / d + 2 : (r - g) / d + 4;
  return { h: h * 60, s, l };
}

function fromHsl({ h, s, l }) {
  const k = n => (n + h / 30) % 12;
  const a = s * Math.min(l, 1 - l);
  const f = n => Math.round(255 * (l - a * Math.max(-1, Math.min(k(n) - 3, 9 - k(n), 1))));
  return { r: f(0), g: f(8), b: f(4) };
}

function format({ r, g, b }, a) {
  if (a >= 1) return '#' + [r, g, b].map(v => v.toString(16).padStart(2, '0')).join('');
  return `rgba(${r}, ${g}, ${b}, ${+a.toFixed(3)})`;
}

// mode 'surface': invert light colours only; mode 'text': invert dark colours only.
function convert(text, mode) {
  const c = parseColor(text);
  if (!c) return text;
  const hsl = toHsl(c);
  if (mode === 'surface' && hsl.l <= .55) return text;
  if (mode === 'text' && hsl.l >= .5) return text;
  const l = mode === 'surface' ? .12 + (1 - hsl.l) * .85 : Math.min(.93, .97 - hsl.l * .55);
  // Neutral greys take the navy hue of the shell so surfaces and tokens match.
  const neutral = hsl.s < .12;
  const h = neutral ? 220 : hsl.h;
  const s = mode === 'surface' ? (neutral ? .3 : Math.min(hsl.s, .45)) : hsl.s;
  return format(fromHsl({ h, s, l }), c.a);
}

const colorPattern = /#[0-9a-fA-F]{3,8}\b|rgba?\([^)]*\)|\b(?:white|black)\b/g;
function darkDeclaration(prop, value) {
  if (prop.startsWith('--')) return tokens[prop] ? `${prop}: ${tokens[prop]}` : null;
  if (/shadow|outline/.test(prop)) return null;
  const mode = prop === 'color' || prop === 'caret-color' || prop === 'fill' ? 'text'
    : /^(background|border|scrollbar-color)/.test(prop) ? 'surface' : null;
  if (!mode) return null;
  const next = value.replace(colorPattern, m => convert(m, mode));
  return next === value ? null : `${prop}: ${next}`;
}

function prefixSelectors(selectorList) {
  return selectorList.split(',').map(s => s.trim()).map(s =>
    s.startsWith(':root') ? prefix + s.slice(5) : s.startsWith('html') ? prefix + s.slice(4) : `${prefix} ${s}`).join(',\n');
}

function splitDeclarations(body) {
  const out = []; let depth = 0, start = 0;
  for (let i = 0; i <= body.length; i++) {
    const ch = body[i];
    if (ch === '(') depth++;
    else if (ch === ')') depth--;
    else if ((ch === ';' && depth === 0) || i === body.length) { out.push(body.slice(start, i)); start = i + 1; }
  }
  return out.map(d => d.trim()).filter(Boolean).map(d => {
    const i = d.indexOf(':');
    return [d.slice(0, i).trim(), d.slice(i + 1).trim()];
  });
}

function walk(css, emit) {
  let i = 0;
  while (i < css.length) {
    const open = css.indexOf('{', i);
    if (open < 0) break;
    const head = css.slice(i, open).trim();
    if (head.startsWith('@')) {
      let depth = 1, j = open + 1;
      while (depth > 0 && j < css.length) { if (css[j] === '{') depth++; else if (css[j] === '}') depth--; j++; }
      const inner = css.slice(open + 1, j - 1);
      if (head.startsWith('@media')) {
        const nested = [];
        walk(inner, (sel, decls) => nested.push([sel, decls]));
        if (nested.length) emit(head, nested);
      }
      i = j;
      continue;
    }
    const close = css.indexOf('}', open);
    emit(head, css.slice(open + 1, close));
    i = close + 1;
  }
}

function rulesFor(selector, body) {
  if (alreadyDark.test(selector)) return '';
  const decls = splitDeclarations(body).map(([p, v]) => darkDeclaration(p, v)).filter(Boolean);
  if (selector.trim() === ':root') decls.push(extraRoot);
  return decls.length ? `${prefixSelectors(selector)} {\n  ${decls.join(';\n  ')};\n}\n` : '';
}

let out = '/* GENERATED by scripts/generate-dark-theme.mjs from ' + sources.join(', ') + ' - do not edit by hand. */\n';
for (const file of sources) {
  const css = readFileSync(join(root, file), 'utf8').replace(/\/\*[\s\S]*?\*\//g, '');
  out += `\n/* ${file} */\n`;
  walk(css, (head, body) => {
    if (Array.isArray(body)) {
      const inner = body.map(([s, b]) => rulesFor(s, b)).join('');
      if (inner) out += `${head} {\n${inner}}\n`;
    } else {
      out += rulesFor(head, body);
    }
  });
}

out += handWritten;

if (process.argv.includes('--check')) {
  const current = (() => { try { return readFileSync(output, 'utf8'); } catch { return ''; } })();
  if (current !== out) {
    console.error('src/theme-dark.css is out of date. Run: node scripts/generate-dark-theme.mjs');
    process.exit(1);
  }
} else {
  writeFileSync(output, out);
  console.log(`wrote ${output} (${out.length} bytes)`);
}
