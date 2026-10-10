import type {
  EditorEntity, EditorMobyPropertyDescriptor, EditorMobyPVarFieldDescriptor,
} from '../types/EditorRuntime.js';

export function compatibleMobyProperties(entities: readonly EditorEntity[]): EditorMobyPropertyDescriptor[] {
  if (!entities.length || entities.some((entity) => !entity.mobyProperties?.length)) return [];
  const sourceClassId = entities[0].sourceClassId;
  if (sourceClassId === undefined || entities.some((entity) => entity.sourceClassId !== sourceClassId)) return [];
  return entities[0].mobyProperties!.filter((descriptor) => entities.every((entity) => {
    const candidate = entity.mobyProperties!.find((value) => value.key === descriptor.key);
    return candidate !== undefined && sameMobyPropertyDescriptor(candidate, descriptor);
  }));
}

export function filterMobyPVarFields(
  fields: readonly EditorMobyPVarFieldDescriptor[],
  search: string,
): EditorMobyPVarFieldDescriptor[] {
  const query = search.trim().toLocaleLowerCase();
  if (!query) return fields.slice();
  return fields.flatMap((field) => {
    const children = filterMobyPVarFields(field.children, query);
    const matches = [field.path, field.label, field.help ?? '', `${field.offset}`, `0x${field.offset.toString(16)}`]
      .some((value) => value.toLocaleLowerCase().includes(query));
    return matches || children.length ? [{ ...field, children }] : [];
  });
}

function sameMobyPropertyDescriptor(
  left: EditorMobyPropertyDescriptor,
  right: EditorMobyPropertyDescriptor,
): boolean {
  return left.key === right.key
    && left.label === right.label
    && left.value.kind === right.value.kind
    && left.editable === right.editable
    && left.integerMinimum === right.integerMinimum
    && left.integerMaximum === right.integerMaximum
    && left.floatMinimum === right.floatMinimum
    && left.floatMaximum === right.floatMaximum
    && left.unit === right.unit
    && left.help === right.help
    && left.readOnlyReason === right.readOnlyReason;
}
