import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { runInNewContext } from 'node:vm';

const ts = createRequire(import.meta.url)('typescript');
const node = (type, props, key) => ({ type, props, key });

export function createRendererHarness({ forge = {}, dependencies = () => undefined } = {}) {
  let slots = [], cursor = 0, effects = [], dirty = false;
  const changed = (before, after) => !before || after.some((value, index) => !Object.is(value, before[index]));
  const react = {
    useState(initial) {
      const index = cursor++, slot = slots[index] ??= { value: typeof initial === 'function' ? initial() : initial };
      return [slot.value, value => {
        const next = typeof value === 'function' ? value(slot.value) : value;
        if (!Object.is(next, slot.value)) { slot.value = next; dirty = true; }
      }];
    },
    useRef(current) { return slots[cursor++] ??= { current }; },
    useMemo(callback, deps) {
      const index = cursor++;
      if (changed(slots[index]?.deps, deps)) slots[index] = { deps, value: callback() };
      return slots[index].value;
    },
    useCallback(callback, deps) { return react.useMemo(() => callback, deps); },
    useEffect(effect, deps) {
      const index = cursor++, previous = slots[index];
      if (!changed(previous?.deps, deps)) return;
      const slot = slots[index] = { deps };
      effects.push(() => { previous?.cleanup?.(); slot.cleanup = effect(); });
    },
  };
  return {
    load(filename, exportName = filename.split('/').at(-1)) {
      const output = ts.transpileModule(readFileSync(new URL(`../../src/renderer/${filename}.tsx`, import.meta.url), 'utf8'), {
        compilerOptions: { module: ts.ModuleKind.CommonJS, target: ts.ScriptTarget.ES2022, jsx: ts.JsxEmit.ReactJSX },
      }).outputText;
      const exports = {};
      runInNewContext(output, {
        exports, setTimeout, clearTimeout, AbortController, DOMException, window: { forge },
        require: specifier => {
          if (specifier === 'react') return react;
          if (specifier === 'react/jsx-runtime') return { jsx: node, jsxs: node };
          if (specifier === '@mantine/core') return new Proxy({}, { get: (_, key) => key });
          if (specifier.endsWith('/UseLog.ts')) return { useLoggedMessage: () => react.useState('') };
          return dependencies(specifier) ?? new Proxy({}, { get: (_, key) => key });
        },
      });
      return exports[exportName];
    },
    render(component, props) {
      let tree;
      for (let index = 0; index < 30; index++) {
        cursor = 0; effects = []; dirty = false;
        tree = component(props);
        effects.forEach(effect => effect());
        if (!dirty) return tree;
      }
      throw new Error('Component did not settle');
    },
    dispose() { slots.forEach(slot => slot?.cleanup?.()); slots = []; },
  };
}

export function find(element, predicate) {
  if (!element || typeof element !== 'object') return;
  if (predicate(element)) return element;
  for (const child of [element.props?.children].flat(Infinity)) {
    const match = find(child, predicate);
    if (match) return match;
  }
}
