// Builds the React UI into the VSIX's WebView folder.
//
// The output lands directly in src/Tootega.Cockpit/WebView/, which the csproj packages as
// content and CockpitWebView serves through a virtual host. That is why there is no copy
// step: the bundle IS the deployed asset.
//
// Usage: node esbuild.mjs [--watch] [--production]
import * as esbuild from 'esbuild';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.resolve(here, '..', 'src', 'Tootega.Cockpit', 'WebView');

const watch = process.argv.includes('--watch');
const production = process.argv.includes('--production');

/** @type {import('esbuild').BuildOptions} */
const options = {
  entryPoints: [path.join(here, 'src', 'main.tsx')],
  outfile: path.join(outDir, 'main.js'),
  bundle: true,
  // An IIFE, not a module: the page is served from a virtual host and loads one script
  // tag, so there is nothing to gain from module resolution at runtime.
  format: 'iife',
  platform: 'browser',
  target: ['chrome110'], // WebView2 is evergreen Chromium
  jsx: 'automatic',
  loader: { '.css': 'css' },
  sourcemap: !production,
  minify: production,
  logLevel: 'info',
  define: {
    'process.env.NODE_ENV': production ? '"production"' : '"development"',
  },
};

if (watch) {
  const context = await esbuild.context(options);
  await context.watch();
  console.log('[webview] watching…');
} else {
  await esbuild.build(options);
  console.log(`[webview] built into ${outDir}`);
}
