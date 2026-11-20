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
//                scenarios in open changes are counted as pending, and built scenarios an open
//                change removes need no test until it is archived

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

// The most recently archived design that defines them holds the failure kinds and settings that are
// built: the code must match it. Kinds and settings that only an open change defines are pending, and
// so are built ones that an open change drops (each open design carries its full §5 and §8 tables).
const builtKinds = new Set();
const builtSettings = new Set();
const archiveDir = join(changesDir, 'archive');
if (existsSync(archiveDir)) {
  const archived = readdirSync(archiveDir).filter((d) => statSync(join(archiveDir, d)).isDirectory()).sort().reverse();
  for (const name of archived) {
    const designPath = join(archiveDir, name, 'design.md');
    if (!existsSync(designPath)) continue;
    const design = read(designPath);
    if (!builtKinds.size) {
      const kinds = firstColumnCodes(section(design, 5));
      if (kinds.length) { kinds.forEach((k) => builtKinds.add(k)); notes.push(`kinds: built set from archived ${name}`); }
    }
    if (!builtSettings.size) {
      const settings = firstColumnCodes(section(design, 8));
      if (settings.length) { settings.forEach((s) => builtSettings.add(s)); notes.push(`settings: built set from archived ${name}`); }
    }
    if (builtKinds.size && builtSettings.size) break;
  }
}
const target = (open, built) => (open.size ? open : built);
const targetKinds = target(allKinds, builtKinds);
const targetSettings = target(allSettings, builtSettings);

// ------------------------------------------------------------------ code
const sourceFiles = walk(join(root, 'src'), (p) => p.endsWith('.cs'));
if (sourceFiles.length && targetKinds.size) {
  const source = sourceFiles.map(read).join('\n');
  const pendingKinds = [];
  for (const kind of targetKinds) {
    if (source.includes(`"${kind}"`)) continue;
    if (builtKinds.has(kind)) errors.push(`src: failure kind "${kind}" from design.md §5 is not in the source`);
    else pendingKinds.push(kind);
  }
  if (pendingKinds.length) notes.push(`kinds: pending in open changes, not yet in the source: ${pendingKinds.join(', ')}`);
}
const optionsFile = sourceFiles.find((p) => p.endsWith('InventoryApiOptions.cs'));
if (optionsFile && targetSettings.size) {
  const props = new Set(
    [...read(optionsFile).matchAll(/public\s+[\w<>?,\s\[\]]+\s+(\w+)\s*\{\s*get;/g)].map((m) => m[1]),
  );
  const pendingSettings = [];
  for (const s of targetSettings) {
    if (props.has(s)) continue;
    if (builtSettings.has(s)) errors.push(`${rel(optionsFile)}: setting ${s} from design.md §8 has no property`);
    else pendingSettings.push(s);
  }
  for (const p of props) {
    if (targetSettings.has(p)) continue;
    if (builtSettings.has(p)) pendingSettings.push(`${p} (dropped)`);
    else errors.push(`${rel(optionsFile)}: property ${p} is not in design.md §8`);
  }
  if (pendingSettings.length) notes.push(`settings: pending in open changes: ${pendingSettings.join(', ')}`);
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

// Built scenarios that an open change retires: every scenario of a requirement it removes, and those a
// modified requirement no longer has. They need no test while the change is open; once it is archived
// they leave openspec/specs and a test that still names one is reported as naming no spec.
function requirementScenarios(text, capability) {
  const byRequirement = new Map();
  for (const block of text.split(/^(?=### Requirement:)/m)) {
    const name = /^### Requirement:\s*(.+?)\s*$/m.exec(block)?.[1];
    if (!name) continue;
    byRequirement.set(name, [...block.matchAll(/^#### Scenario:\s*(.+?)\s*$/gm)].map((m) => `${capability}/${m[1]}`));
  }
  return byRequirement;
}
const retiring = new Set();
for (const change of changeDirs) {
  for (const delta of walk(join(changesDir, change, 'specs'), (p) => p.endsWith('spec.md'))) {
    const capability = delta.split(sep).at(-2);
    const builtPath = join(root, 'openspec', 'specs', capability, 'spec.md');
    if (!existsSync(builtPath)) continue;
    const builtRequirements = requirementScenarios(read(builtPath), capability);
    const text = read(delta);
    for (const [heading, keep] of [['REMOVED', false], ['MODIFIED', true]]) {
      const start = text.search(new RegExp(`^## ${heading} Requirements`, 'm'));
      if (start < 0) continue;
      const rest = text.slice(start + 3);
      const end = rest.search(/^## /m);
      const part = end < 0 ? rest : rest.slice(0, end);
      for (const [name, changed] of requirementScenarios(part, capability)) {
        for (const s of builtRequirements.get(name) ?? []) if (!keep || !changed.includes(s)) retiring.add(s);
      }
    }
  }
}

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
  if (!tested.has(name) && !retiring.has(name)) errors.push(`openspec/specs: built scenario "${name}" has no test`);
}
const pendingUntested = [...pending].filter((s) => !tested.has(s) && !built.has(s));
const retiringTested = [...retiring].filter((s) => tested.has(s));
notes.push(`scenarios: ${built.size} built, ${pending.size} in open changes, ${tested.size} referenced by tests, ${pendingUntested.length} pending without a test`);
if (retiring.size) notes.push(`scenarios: ${retiring.size} built scenario(s) retired by open changes, ${retiringTested.length} still named by tests`);

// ------------------------------------------------------------------ report
for (const n of notes) console.log(`note   ${n}`);
for (const e of errors) console.log(`error  ${e}`);
console.log(errors.length ? `\n${errors.length} drift error(s).` : '\nNo drift.');
process.exit(errors.length ? 1 : 0);
