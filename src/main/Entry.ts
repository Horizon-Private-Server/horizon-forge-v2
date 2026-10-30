// GPU workers start without loading editor services or acquiring its instance lock.
const entry = process.argv.includes('--forge-occlusion-worker')
  ? '../occlusion/Main.cjs'
  : './main.js';
await import(new URL(entry, import.meta.url).href);
export {};
