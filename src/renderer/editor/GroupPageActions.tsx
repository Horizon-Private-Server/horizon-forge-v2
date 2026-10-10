import { CaretLeftIcon } from '@phosphor-icons/react/dist/csr/CaretLeft';
import { CaretRightIcon } from '@phosphor-icons/react/dist/csr/CaretRight';
import { ActionIcon } from '@mantine/core';

export function GroupPageActions({ label, page, pageCount, start, end, total, onChange }: {
  label: string;
  page: number;
  pageCount: number;
  start: number;
  end: number;
  total: number;
  onChange(page: number): void;
}) {
  if (pageCount <= 1) return null;
  return <>
    <ActionIcon
      aria-label={`Previous page of ${label}`}
      disabled={page === 0}
      size="xs"
      title={`${start + 1}–${end} of ${total}`}
      variant="subtle"
      onClick={(event) => { event.stopPropagation(); onChange(page - 1); }}
    ><CaretLeftIcon size={13} /></ActionIcon>
    <ActionIcon
      aria-label={`Next page of ${label}`}
      disabled={page + 1 >= pageCount}
      size="xs"
      title={`${start + 1}–${end} of ${total}`}
      variant="subtle"
      onClick={(event) => { event.stopPropagation(); onChange(page + 1); }}
    ><CaretRightIcon size={13} /></ActionIcon>
  </>;
}
