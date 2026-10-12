import type { ReactNode } from 'react';
import { Paper, Text, ThemeIcon, Title } from '@mantine/core';
import classes from './editor.module.css';

interface SectionProps {
  title: ReactNode;
  description?: ReactNode;
  icon?: ReactNode;
  /** Content at the right of the title (e.g. a button). */
  aside?: ReactNode;
  children: ReactNode;
}

/** A titled card that groups related settings. */
export function Section({ title, description, icon, aside, children }: SectionProps) {
  return (
    <Paper withBorder p={{ base: 'md', sm: 'lg' }} className={classes.section} component="section">
      <div className={classes.sectionHead}>
        {icon && (
          <ThemeIcon variant="light" size={34} radius="md" aria-hidden>
            {icon}
          </ThemeIcon>
        )}
        <div className={classes.sectionTitle}>
          <Title order={3} size="h5">
            {title}
          </Title>
          {description && (
            <Text size="xs" c="dimmed" mt={2}>
              {description}
            </Text>
          )}
        </div>
        {aside}
      </div>
      {children}
    </Paper>
  );
}
