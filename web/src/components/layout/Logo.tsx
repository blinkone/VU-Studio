import { Group, Text } from '@mantine/core';

export function Logo() {
  return (
    <Group gap={10} wrap="nowrap">
      <svg width="30" height="30" viewBox="0 0 64 64" aria-hidden>
        <rect width="64" height="64" rx="14" fill="var(--mantine-color-dark-7)" />
        <path d="M12 44a20 20 0 0 1 40 0" fill="none" stroke="var(--mantine-color-amber-5)" strokeWidth="4" strokeLinecap="round" />
        <path d="M32 46 44 24" stroke="var(--mantine-color-gray-0)" strokeWidth="4" strokeLinecap="round" />
        <circle cx="32" cy="46" r="4" fill="var(--mantine-color-gray-0)" />
      </svg>
      <Text fw={700} size="lg" style={{ letterSpacing: '0.02em' }}>
        VU Studio
      </Text>
    </Group>
  );
}
