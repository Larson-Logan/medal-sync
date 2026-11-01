#!/usr/bin/env node
// Local file and Markdown section validation. Remote URLs and prose code paths
// are not fetched. Explicit files avoid rewriting history.
import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { dirname, resolve, basename } from 'node:path';

const root = process.cwd();
const files = process.argv.slice(2);
if (!files.length) {
  console.error('Usage: node scripts/check-doc-links.mjs <authored.md> [...]');
  process.exit(2);
}
const wiki = [];
function walk(dir) {
  if (!existsSync(dir)) return;
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const path = resolve(dir, entry.name);
    if (entry.isDirectory()) { if (!['.obsidian', '.raw'].includes(entry.name)) walk(path); }
    else if (entry.name.endsWith('.md')) wiki.push(path);
  }
}
walk(resolve(root, 'wiki'));

function withoutFencedCode(source) {
  let fence = null;
  return source.split(/\r?\n/).map(line => {
    const quoted = line.match(/^(?: {0,3}>[ \t]?)+/);
    const depth = quoted ? (quoted[0].match(/>/g) ?? []).length : 0;
    const content = quoted ? line.slice(quoted[0].length) : line;
    // Leaving the quote container also ends a fence inside that container.
    if (fence && depth < fence.depth) fence = null;
    if (fence) {
      const close = content.match(/^ {0,3}(`+|~+)[ \t]*$/);
      if (depth === fence.depth && close && close[1][0] === fence.marker[0] && close[1].length >= fence.marker.length) fence = null;
      return '';
    }
    const open = content.match(/^ {0,3}(`{3,}|~{3,})(.*)$/);
    if (open && (open[1][0] !== '`' || !open[2].includes('`'))) {
      fence = { marker: open[1], depth };
      return '';
    }
    return line;
  }).join('\n');
}

function entities(text) {
  const named = { amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: '\u00a0' };
  return text.replace(/&(#x[0-9a-f]+|#\d+|amp|lt|gt|quot|apos|nbsp);/gi, (original, entity) => {
    if (!entity.startsWith('#')) return named[entity.toLowerCase()];
    const point = entity[1].toLowerCase() === 'x' ? parseInt(entity.slice(2), 16) : Number(entity.slice(1));
    return point > 0 && point <= 0x10ffff ? String.fromCodePoint(point) : original;
  });
}

const anchorCache = new Map();
function anchors(path) {
  if (anchorCache.has(path)) return anchorCache.get(path);
  // A marker prevents removal from forming new markup across token boundaries.
  // It is discarded with other non-slug characters after heading interpretation.
  const source = withoutFencedCode(readFileSync(path, 'utf8')).replace(/<!--[\s\S]*?-->/g, '\0');
  const result = new Set(), generated = new Set();
  // Custom anchors do not participate in automatic heading numbering.
  // Ignore inline code so an example `<a id="example">` is not an anchor.
  const html = source.replace(/(`+)[\s\S]*?\1/g, '').replace(/^(?: {4}|\t).*$/gm, '');
  for (const tag of html.matchAll(/<a\s+[^>]*>/gi)) {
    for (const attr of tag[0].matchAll(/([a-z_:][\w:.-]*)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>]+))/gi))
      if (/^(id|name)$/i.test(attr[1])) result.add(entities(attr[2] ?? attr[3] ?? attr[4]));
  }
  const lines = source.split(/\r?\n/);
  for (let i = 0; i < lines.length; i++) {
    const line = lines[i].replace(/^ {0,3}(?:>[ \t]?)+/, '');
    const atx = line.match(/^ {0,3}#{1,6}(?:[ \t]+(.*)|[ \t]*)$/);
    let heading;
    if (atx) heading = (atx[1] ?? '').replace(/[ \t]+#+[ \t]*$/, '');
    else if (i + 1 < lines.length && /^ {0,3}(?:=+|-+)[ \t]*$/.test(lines[i + 1])
        && line.trim() && !/^(?: {4}|\t)/.test(line)) {
      heading = line;
      i++;
    } else continue;
    // Code contents are literal: `_name_` is not emphasis and `<tag>` is not HTML.
    const code = [];
    heading = heading.replace(/(`+)([^`]*?)\1/g, (_, fence, value) => `\0${code.push(value) - 1}\0`);
    const escaped = [];
    heading = heading.replace(/\\([!"#$%&'()*+,\-./:;<=>?@[\]\\^_`{|}~])/g,
      (_, value) => `\u0001${escaped.push(value) - 1}\u0001`);
    // Use rendered link labels and inline formatting contents, not destinations.
    heading = heading.replace(/!?\[([^\]]*)\]\([^)]*\)/g, '$1')
      .replace(/\[([^\]]+)\]\[[^\]]*\]/g, '$1')
      .replace(/<[^>]+>/g, '\0').replace(/`+/g, '')
      .replace(/(?<!\w)_{1,2}(.+?)_{1,2}(?!\w)/g, '$1');
    heading = entities(heading).replace(/\0(\d+)\0/g, (_, index) => code[Number(index)]);
    heading = heading.replace(/\u0001(\d+)\u0001/g, (_, index) => escaped[Number(index)]);
    const base = heading.trim().toLowerCase().replace(/[^\p{L}\p{M}\p{N}_ -]/gu, '').replace(/ /g, '-');
    let slug = base, suffix = 0;
    while (generated.has(slug)) slug = `${base}-${++suffix}`;
    generated.add(slug);
    result.add(slug);
  }
  anchorCache.set(path, result);
  return result;
}

let failures = 0, checked = 0, checkedAnchors = 0;
for (const file of files) {
  const path = resolve(root, file);
  // Inline code becomes a placeholder, not '', so [`file.md`](path) keeps its link text
  // while literal syntax such as `[[inputs.exec]]` is no longer read as a wikilink.
  const source = withoutFencedCode(readFileSync(path, 'utf8')).replace(/`[^`\n]+`/g, 'code');
  const links = [...source.matchAll(/(?<!!)\[[^\]\n]+\]\((<[^>]+>|[^\s)]+)(?:\s+"[^"]*")?\)/g)].map(match => ({ target: match[1].replace(/^<|>$/g, ''), wiki: false }));
  // Full, collapsed and shortcut reference links share these definitions.
  // Check each definition's destination even if the reference is unused.
  // Four-space/tab indentation is code; [^label] introduces a GFM footnote.
  links.push(...[...source.matchAll(/^ {0,3}\[(?!\^)[^\]\n]+\]:[ \t]*(?:\n[ \t]*)?(<[^>\n]*>|[^\s<>]+)/gm)]
    .map(match => ({ target: match[1].replace(/^<|>$/g, ''), wiki: false })));
  links.push(...[...source.matchAll(/\[\[([^\]]+)\]\]/g)].map(match => ({ target: match[1].split('|')[0], wiki: true })));
  for (const link of links) {
    if (/^(https?:|mailto:|app:)/i.test(link.target)) continue;
    const hash = link.target.indexOf('#');
    let target, fragment;
    try {
      target = decodeURIComponent(hash < 0 ? link.target : link.target.slice(0, hash));
      fragment = hash < 0 ? '' : decodeURIComponent(link.target.slice(hash + 1));
    } catch {
      failures++; console.error(`${file}: invalid URL encoding in ${link.target}`); continue;
    }
    if (!target && !fragment) continue;
    if (target) checked++;
    let destination;
    if (!target) destination = path;
    if (link.wiki) {
      const name = target.endsWith('.md') ? target : target + '.md';
      const matches = wiki.filter(item => basename(item) === name);
      destination = !target ? path : [resolve(dirname(path), name), resolve(root, 'wiki', name), ...(matches.length === 1 ? matches : [])].find(existsSync);
    } else if (target && !/^file:/i.test(target)) {
      const candidate = target.startsWith('/') ? resolve(root, '.' + target) : resolve(dirname(path), target);
      if (existsSync(candidate)) destination = candidate;
    }
    if (!destination) { failures++; console.error(`${file}: unresolved file target ${link.target}`); continue; }
    if (fragment && /\.md$/i.test(destination)) {
      checkedAnchors++;
      if (!anchors(destination).has(fragment)) { failures++; console.error(`${file}: unresolved section anchor ${link.target}`); }
    }
  }
}
console.log(`${checked} local file targets and ${checkedAnchors} Markdown anchors checked; ${failures} unresolved. Remote URLs not checked.`);
process.exitCode = failures ? 1 : 0;
