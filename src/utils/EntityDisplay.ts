import type { EditorEntity } from '../types/EditorRuntime.js';

export function editorEntityDisplayName(entity: EditorEntity): string {
  if (!entity.sourceClassName) return entity.name;
  const generated = /^Moby 0x[\da-f]+( #\d+)$/i.exec(entity.name);
  if (generated) return `${entity.sourceClassName}${generated[1]}`;
  return /^moby:0x[\da-f]+$/i.test(entity.name)
    ? entity.sourceClassName
    : entity.name;
}
