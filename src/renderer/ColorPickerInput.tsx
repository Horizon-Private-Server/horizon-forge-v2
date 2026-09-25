import { Box, Code, ColorPicker, Popover, Stack, Text, UnstyledButton } from '@mantine/core';

interface ColorPickerInputProps {
  label: string;
  value: string;
  format: 'hex' | 'rgb';
  disabled?: boolean;
  onChange(value: string): void;
  onChangeEnd(value: string): void;
}

export function ColorPickerInput({
  label, value, format, disabled = false, onChange, onChangeEnd,
}: ColorPickerInputProps) {
  return <Stack gap={2}>
    <Text size="xs" fw={500}>{label}</Text>
    <Popover position="bottom-start" shadow="md">
      <Popover.Target>
        <UnstyledButton
          className="color-picker-input"
          disabled={disabled}
          aria-label={`${label}: ${value}`}
        >
          <Box className="color-picker-preview" style={{ backgroundColor: value }} />
          <Code>{value}</Code>
        </UnstyledButton>
      </Popover.Target>
      <Popover.Dropdown>
        <ColorPicker
          value={value}
          format={format}
          withPicker
          onChange={onChange}
          onChangeEnd={onChangeEnd}
        />
      </Popover.Dropdown>
    </Popover>
  </Stack>;
}
