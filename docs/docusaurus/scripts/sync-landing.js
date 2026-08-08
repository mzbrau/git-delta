#!/usr/bin/env node
'use strict';

const fs = require('fs');
const path = require('path');

const root = path.resolve(__dirname, '..');
const src = path.join(root, '..', 'img', 'landing');
const dest = path.join(root, 'static', 'img', 'landing');

fs.mkdirSync(dest, { recursive: true });

if (!fs.existsSync(src)) {
  console.error(`sync-landing: source directory not found: ${src}`);
  process.exit(1);
}

const files = fs.readdirSync(src).filter((name) => name.endsWith('.png'));
if (files.length === 0) {
  console.error(`sync-landing: no PNGs found in ${src}`);
  process.exit(1);
}

for (const name of files) {
  fs.copyFileSync(path.join(src, name), path.join(dest, name));
}

console.log(`sync-landing: copied ${files.length} PNG(s) → static/img/landing/`);
