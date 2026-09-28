import { createTheme } from '@mantine/core';

import type { UiSize } from '../types/ForgeApi.js';
import { nextUiSize, UI_SIZES } from '../utils/UiSize.ts';

const SPACING: Record<UiSize, string> = {
  xs: '0.25rem', sm: '0.375rem', md: '0.5rem', lg: '0.75rem', xl: '1rem',
};
const FONT_SIZES: Record<UiSize, string> = {
  xs: '0.6875rem', sm: '0.75rem', md: '0.8125rem', lg: '0.9375rem', xl: '1.0625rem',
};
const LINE_HEIGHTS: Record<UiSize, string> = {
  xs: '1.2', sm: '1.25', md: '1.3', lg: '1.35', xl: '1.4',
};
const RADIUS: Record<UiSize, string> = {
  xs: '0.125rem', sm: '0.1875rem', md: '0.25rem', lg: '0.375rem', xl: '0.5rem',
};

export function createForgeTheme(size: UiSize) {
  const largeSize = nextUiSize(size);
  return createTheme({
  cursorType: 'pointer',
  defaultRadius: size,
  spacing: floorScale(SPACING, size),
  fontSizes: floorScale(FONT_SIZES, size),
  lineHeights: floorScale(LINE_HEIGHTS, size),
  radius: floorScale(RADIUS, size),
  components: {
    ActionIcon: { defaultProps: { size: largeSize } },
    Alert: { defaultProps: { p: size } },
    Badge: { defaultProps: { size: largeSize } },
    Button: { defaultProps: { size: `compact-${largeSize}` } },
    Checkbox: { defaultProps: { size } },
    Group: { defaultProps: { gap: size } },
    Modal: {
      defaultProps: { padding: largeSize },
      styles: {
        header: { minHeight: 'auto' },
        title: {
          fontWeight: 600,
          lineHeight: 'var(--mantine-line-height-md)',
        },
      },
    },
    Menu: {
      defaultProps: { shadow: 'md' },
      styles: {
        dropdown: { padding: 'var(--mantine-spacing-xs)' },
        item: { minHeight: 'var(--forge-ui-row-height)', padding: 'var(--mantine-spacing-xs) var(--mantine-spacing-md)' },
      },
    },
    MultiSelect: { defaultProps: { size } },
    NumberInput: { defaultProps: { size } },
    Radio: { defaultProps: { size } },
    Select: { defaultProps: { size } },
    SegmentedControl: { defaultProps: { size } },
    Slider: { defaultProps: { size } },
    Stack: { defaultProps: { gap: size } },
    Switch: { defaultProps: { size } },
    Tabs: { defaultProps: { variant: 'default' } },
    Textarea: { defaultProps: { size } },
    TextInput: { defaultProps: { size } },
  },
  });
}

function floorScale(values: Record<UiSize, string>, minimum: UiSize): Record<UiSize, string> {
  const minimumIndex = UI_SIZES.indexOf(minimum);
  return Object.fromEntries(UI_SIZES.map((key, index) => [
    key,
    values[UI_SIZES[Math.max(index, minimumIndex)]],
  ])) as Record<UiSize, string>;
}
