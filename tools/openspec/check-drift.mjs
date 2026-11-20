#!/usr/bin/env node
// Drift checks that `openspec validate` does not do: cross-references inside a change, and the
// link between specs and the code and tests that implement them.
//
//   node tools/openspec/check-drift.mjs            errors fail the run, notes are informational
//
// Checks
//   decisions    every D<n> referenced in a change is defined in its design.md decision table, and
//                every "D1–Dn" range names the real last decision
//   sections     every "design.md §N" points at an existing "## N." heading
//   kinds        every backticked failure kind in a change's specs is in design.md §5, and (once
//                src/ exists) every kind in §5 appears in the source
//   settings     every `InventoryApi:Name` in specs is in design.md §8, and (once the options class
//                exists) its properties match §8 exactly
//   scenarios    every [Trait("Scenario", "<capability>/<title>")] in tests names a real scenario;
//                every scenario in openspec/specs (built behaviour) has at least one such test;
//                scenarios in open changes are counted as pending

import { readFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import { join, relative, sep } from 'node:path';

const root = process.cwd();
const errors = [];
const notes = [];
const rel = (p) => relative(root, p).split(sep).join('/');

function walk(dir, filter, out = []) {
  if (!existsSync(dir)) return out;
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name === 'bin' || name === 'obj' || name.startsWith('.')) continue;
    const p = join(dir, name);
    if (statSync(p).isDirectory()) walk(p, filter, out);
    else if (filter(p)) out.push(p);
  }
  return out;
}
const read = (p) => readFileSync(p, 'utf8');

function section(md, number) {
  const re = new RegExp(`^## ${number}\\.[^\\n]*\\n([\\s\\S]*?)(?=^## \\d+\\.|$(?![\\s\\S]))`, 'm');
  return re.exec(md)?.[1] ?? null;
}
const firstColumnCodes = (text) =>
  [...(text ?? '').matchAll(/^\|\s*`([^`]+)`\s*\|/gm)].map((m) => m[1]);

// ------------------------------------------------------------------ changes
const changesDir = join(root, 'openspec', 'changes');
const changeDirs = existsSync(changesDir)
  ? readdirSync(changesDir).filter((d) => d !== 'archive' && statSync(join(changesDir, d)).isDirectory())
  : [];

const capabilityNames = new Set([
  ...walk(join(root, 'openspec'), (p) => p.endsWith('spec.md')).map((p) => p.split(sep).at(-2)),
  ...changeDirs,
]);

let allKinds = new Set();
let allSettings = new Set();

for (const change of changeDirs) {
  const dir = join(changesDir, change);
  const designPath = join(dir, 'design.md');
  const design = existsSync(designPath) ? read(designPath) : '';
  const docs = walk(dir, (p) => p.endsWith('.md'));

  // decisions
  const defined = new Set([...design.matchAll(/^\|\s*(D\d+)\s*\|/gm)].map((m) => m[1]));
  const maxDefined = Math.max(0, ...[...defined].map((d) => Number(d.slice(1))));
  for (const doc of docs) {
    const text = read(doc);
    for (const m of text.matchAll(/\bD(\d+)\s*[–-]\s*D(\d+)\b/g)) {
      if (Number(m[1]) === 1 && Number(m[2]) !== maxDefined) {
        errors.push(`${rel(doc)}: range ${m[0]} but design.md defines D1–D${maxDefined}`);
      }
    }
    if (doc === designPath) continue;
    for (const m of text.matchAll(/\bD(\d+)\b/g)) {
      if (!defined.has(`D${m[1]}`)) errors.push(`${rel(doc)}: D${m[1]} is not defined in design.md`);
    }
  }

  // sections
  for (const doc of docs) {
    for (const m of read(doc).matchAll(/design\.md`?\s*§(\d+)/g)) {
      if (!new RegExp(`^## ${m[1]}\\.`, 'm').test(design)) {
        errors.push(`${rel(doc)}: design.md §${m[1]} does not exist`);
      }
    }
  }

  // kinds
  const kinds = new Set(firstColumnCodes(section(design, 5)));
  kinds.forEach((k) => allKinds.add(k));
  const specFiles = walk(join(dir, 'specs'), (p) => p.endsWith('spec.md'));
  if (kinds.size) {
    for (const spec of specFiles) {
      for (const m of read(spec).matchAll(/`(?:kind: )?([a-z]+(?:-[a-z]+)+)`/g)) {
        if (!kinds.has(m[1]) && !capabilityNames.has(m[1])) {
          errors.push(`${rel(spec)}: failure kind \`${m[1]}\` is not in design.md §5`);
        }
      }
    }
  }

  // settings
  const settings = new Set(firstColumnCodes(section(design, 8)));
  settings.forEach((s) => allSettings.add(s));
  if (settings.size) {
    for (const doc of docs) {
      for (const m of read(doc).matchAll(/InventoryApi[:_]{1,2}([A-Z][A-Za-z]+)/g)) {
        if (!settings.has(m[1])) errors.push(`${rel(doc)}: setting InventoryApi:${m[1]} is not in design.md §8`);
      }
    }
  }
}

// ------------------------------------------------------------------ code
const sourceFiles = walk(join(root, 'src'), (p) => p.endsWith('.cs'));
if (sourceFiles.length && allKinds.size) {
  const source = sourceFiles.map(read).join('\n');
  for (const kind of allKinds) {
    if (!source.includes(`"${kind}"`)) errors.push(`src: failure kind "${kind}" from design.md §5 is not in the source`);
  }
}
const optionsFile = sourceFiles.find((p) => p.endsWith('InventoryApiOptions.cs'));
if (optionsFile && allSettings.size) {
  const props = new Set(
    [...read(optionsFile).matchAll(/public\s+[\w<>?,\s\[\]]+\s+(\w+)\s*\{\s*get;/g)].map((m) => m[1]),
  );
  for (const s of allSettings) if (!props.has(s)) errors.push(`${rel(optionsFile)}: setting ${s} from design.md §8 has no property`);
  for (const p of props) if (!allSettings.has(p)) errors.push(`${rel(optionsFile)}: property ${p} is not in design.md §8`);
}

// ------------------------------------------------------------------ scenarios
function scenarios(specPath) {
  const capability = specPath.split(sep).at(-2);
  return [...read(specPath).matchAll(/^#### Scenario:\s*(.+?)\s*$/gm)].map((m) => `${capability}/${m[1]}`);
}
const built = new Set(walk(join(root, 'openspec', 'specs'), (p) => p.endsWith('spec.md')).flatMap(scenarios));
const pending = new Set(
  changeDirs.flatMap((c) => walk(join(changesDir, c, 'specs'), (p) => p.endsWith('spec.md')).flatMap(scenarios)),
);

const tested = new Map();
for (const file of walk(join(root, 'tests'), (p) => p.endsWith('.cs'))) {
  for (const m of read(file).matchAll(/Trait\(\s*"Scenario"\s*,\s*"([^"]+)"\s*\)/g)) {
    if (!tested.has(m[1])) tested.set(m[1], []);
    tested.get(m[1]).push(rel(file));
  }
}
for (const [name, files] of tested) {
  if (!built.has(name) && !pending.has(name)) errors.push(`${files[0]}: test names scenario "${name}", which no spec defines`);
}
for (const name of built) {
  if (!tested.has(name)) errors.push(`openspec/specs: built scenario "${name}" has no test`);
}
const pendingUntested = [...pending].filter((s) => !tested.has(s) && !built.has(s));
notes.push(`scenarios: ${built.size} built, ${pending.size} in open changes, ${tested.size} referenced by tests, ${pendingUntested.length} pending without a test`);

// ------------------------------------------------------------------ report
for (const n of notes) console.log(`note   ${n}`);
for (const e of errors) console.log(`error  ${e}`);
console.log(errors.length ? `\n${errors.length} drift error(s).` : '\nNo drift.');
process.exit(errors.length ? 1 : 0);
