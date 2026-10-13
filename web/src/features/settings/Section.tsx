import type { ReactNode } from 'react';
import { Card, Group, Stack, Text, ThemeIcon, Title } from '@mantine/core';

interface SectionProps {
  title: string;
  description?: ReactNode;
  icon?: ReactNode;
  /** Content aligned to the right of the heading (a status badge, an action …). */
  right?: ReactNode;
  children: ReactNode;
}

/** A titled card: the building block of every settings tab. */
export function Section({ title, description, icon, right, children }: SectionProps) {
  return (
    <Card padding="lg" component="section" aria-label={title}>
      <Stack gap="md">
        <Group justify="space-between" align="flex-start" wrap="nowrap" gap="sm">
          <Group gap="sm" wrap="nowrap" align="flex-start" style={{ minWidth: 0 }}>
            {icon && (
              <ThemeIcon variant="light" color="gray" size={36} radius="md" style={{ flex: 'none' }}>
                {icon}
              </ThemeIcon>
            )}
            <div style={{ minWidth: 0 }}>
              <Title order={4} lh={1.3}>
                {title}
              </Title>
              {description && (
                <Text size="sm" c="dimmed" mt={2}>
                  {description}
                </Text>
              )}
            </div>
          </Group>
          {right}
        </Group>
        {children}
      </Stack>
    </Card>
  );
}
