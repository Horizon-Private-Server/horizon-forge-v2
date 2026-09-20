import { createTheme } from '@mantine/core';

export const forgeTheme = createTheme({
  cursorType: 'pointer',
  defaultRadius: 'xs',
  spacing: {
    xs: '0.25rem',
    sm: '0.375rem',
    md: '0.5rem',
    lg: '0.75rem',
    xl: '1rem',
  },
  fontSizes: {
    xs: '0.6875rem',
    sm: '0.75rem',
    md: '0.8125rem',
    lg: '0.9375rem',
    xl: '1.0625rem',
  },
  lineHeights: {
    xs: '1.2',
    sm: '1.25',
    md: '1.3',
    lg: '1.35',
    xl: '1.4',
  },
  radius: {
    xs: '0.125rem',
    sm: '0.1875rem',
    md: '0.25rem',
    lg: '0.375rem',
    xl: '0.5rem',
  },
  components: {
    ActionIcon: { defaultProps: { size: 'sm' } },
    Alert: { defaultProps: { p: 'xs' } },
    Badge: { defaultProps: { size: 'sm' } },
    Button: { defaultProps: { size: 'compact-sm' } },
    Checkbox: { defaultProps: { size: 'xs' } },
    Group: { defaultProps: { gap: 'xs' } },
    Modal: {
      defaultProps: { padding: 'sm' },
      styles: {
        header: { minHeight: 'auto' },
        title: {
          fontWeight: 600,
          lineHeight: 'var(--mantine-line-height-md)',
        },
      },
    },
    MultiSelect: { defaultProps: { size: 'xs' } },
    NumberInput: { defaultProps: { size: 'xs' } },
    Radio: { defaultProps: { size: 'xs' } },
    Select: { defaultProps: { size: 'xs' } },
    Stack: { defaultProps: { gap: 'xs' } },
    Switch: { defaultProps: { size: 'xs' } },
    Tabs: { defaultProps: { variant: 'default' } },
    Textarea: { defaultProps: { size: 'xs' } },
    TextInput: { defaultProps: { size: 'xs' } },
  },
});
