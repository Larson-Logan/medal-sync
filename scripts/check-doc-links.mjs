#!/usr/bin/env node
// File-target validation only; headings, remote URLs and prose code paths are
// deliberately not claimed as validated. Explicit files avoid rewriting history.
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
    if (fence) {
      const close = line.match(/^ {0,3}(`+|~+)[ \t]*$/);
      if (close && close[1][0] === fence[0] && close[1].length >= fence.length) fence = null;
      return '';
    }
    const open = line.match(/^ {0,3}(`{3,}|~{3,})(.*)$/);
    if (open && (open[1][0] !== '`' || !open[2].includes('`'))) {
      fence = open[1];
      return '';
    }
    return line;
  }).join('\n');
}

let failures = 0, checked = 0;
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
    if (/^(https?:|mailto:|app:|#)/i.test(link.target)) continue;
    let target;
    try { target = decodeURIComponent(link.target.split('#')[0]); } catch { target = ''; }
    if (!target) continue;
    checked++;
    let valid = false;
    if (link.wiki) {
      const name = target.endsWith('.md') ? target : target + '.md';
      valid = existsSync(resolve(dirname(path), name)) || existsSync(resolve(root, 'wiki', name)) || wiki.filter(item => basename(item) === name).length === 1;
    } else valid = !/^file:/i.test(target) && existsSync(resolve(dirname(path), target));
    if (!valid) { failures++; console.error(`${file}: unresolved file target ${link.target}`); }
  }
}
console.log(`${checked} local file targets checked; ${failures} unresolved. Remote URLs and anchors not checked.`);
process.exitCode = failures ? 1 : 0;
