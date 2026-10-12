import { useMemo, useState } from 'react';
import { Button, CloseButton, Group, Input, Popover, ScrollArea, Stack, Text, TextInput, Tooltip } from '@mantine/core';
import { IconChevronDown, IconSearch, IconSparkles } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { useIcons } from '@/api/queries';
import type { IconInfo } from '@/api/types';
import { MdiIcon } from '@/components/MdiIcon';
import classes from '../FaceEditor.module.css';

const CATEGORY_ORDER = ['hardware', 'cooling', 'power', 'network', 'audio', 'weather', 'home', 'time', 'finance', 'misc'];

export interface AutomaticIcon {
  /** Value stored for the automatic choice (undefined, or "{icon}" in icon layers). */
  value: string | undefined;
  label: string;
  /** Icon it currently resolves to, for the button preview. */
  resolved?: string;
}

interface IconPickerProps {
  value: string | undefined;
  onChange: (value: string | undefined) => void;
  automatic: AutomaticIcon;
  label?: string;
  description?: string;
  size?: 'xs' | 'sm';
}

/** Searchable grid of the bundled Material Design Icons, grouped by category. */
export function IconPicker({ value, onChange, automatic, label, description, size = 'sm' }: IconPickerProps) {
  const { t } = useTranslation('face');
  const { data: icons = [], isLoading } = useIcons();
  const [opened, setOpened] = useState(false);
  const [query, setQuery] = useState('');

  const isAutomatic = value === automatic.value || value === undefined || value === '';
  const shown = isAutomatic ? automatic.resolved : value;

  const groups = useMemo(() => {
    const q = query.trim().toLowerCase().replace(/\s+/g, '-');
    const map = new Map<string, IconInfo[]>();
    for (const icon of icons) {
      if (q && !icon.id.includes(q) && !icon.category.includes(q)) continue;
      if (!map.has(icon.category)) map.set(icon.category, []);
      map.get(icon.category)!.push(icon);
    }
    const rank = (c: string) => (CATEGORY_ORDER.indexOf(c) < 0 ? CATEGORY_ORDER.length : CATEGORY_ORDER.indexOf(c));
    return [...map.entries()].sort(([a], [b]) => rank(a) - rank(b) || a.localeCompare(b));
  }, [icons, query]);

  const choose = (next: string | undefined) => {
    onChange(next);
    setOpened(false);
    setQuery('');
  };

  return (
    <Input.Wrapper label={label} description={description} size={size}>
      <Popover opened={opened} onChange={setOpened} width={344} position="bottom-start" shadow="lg" trapFocus withinPortal>
        <Popover.Target>
          <Button
            variant="default"
            size={size}
            fullWidth
            className={classes.iconButton}
            mt={description ? 4 : undefined}
            onClick={() => setOpened((o) => !o)}
            leftSection={shown ? <MdiIcon id={shown} size={size === 'xs' ? 16 : 18} /> : <IconSparkles size={16} />}
            rightSection={<IconChevronDown size={14} />}
            styles={{ label: { flex: 1, fontWeight: 500 } }}
            aria-haspopup="dialog"
          >
            <Text span size={size} truncate>
              {isAutomatic ? automatic.label : value}
            </Text>
          </Button>
        </Popover.Target>
        <Popover.Dropdown p="xs">
          <Stack gap="xs">
            <TextInput
              size="xs"
              placeholder={t('iconPicker.search')}
              leftSection={<IconSearch size={14} />}
              value={query}
              onChange={(e) => setQuery(e.currentTarget.value)}
              rightSection={query ? <CloseButton size="xs" onClick={() => setQuery('')} aria-label={t('iconPicker.clear')} /> : null}
              data-autofocus
              aria-label={t('iconPicker.search')}
            />
            <Button
              variant={isAutomatic ? 'light' : 'subtle'}
              size="xs"
              justify="flex-start"
              leftSection={automatic.resolved ? <MdiIcon id={automatic.resolved} size={16} /> : <IconSparkles size={16} />}
              onClick={() => choose(automatic.value)}
            >
              {automatic.label}
            </Button>
            <ScrollArea.Autosize mah={300} type="auto" offsetScrollbars>
              <Stack gap="sm">
                {groups.map(([category, list]) => (
                  <Stack key={category} gap={4}>
                    <Text className={classes.sectionTitle}>
                      {t(`iconCategories.${category}`, { defaultValue: category })}
                    </Text>
                    <div className={classes.iconGrid}>
                      {list.map((icon) => (
                        <Tooltip key={icon.id} label={icon.id} openDelay={400}>
                          <button
                            type="button"
                            className={classes.iconCell}
                            data-selected={(!isAutomatic && icon.id === value) || undefined}
                            aria-pressed={!isAutomatic && icon.id === value}
                            aria-label={icon.id}
                            onClick={() => choose(icon.id)}
                          >
                            <MdiIcon id={icon.id} size={20} />
                          </button>
                        </Tooltip>
                      ))}
                    </div>
                  </Stack>
                ))}
                {groups.length === 0 && (
                  <Group justify="center" py="md">
                    <Text size="sm" c="dimmed">
                      {isLoading ? t('common:loading') : icons.length === 0 ? t('iconPicker.none') : t('iconPicker.noMatch')}
                    </Text>
                  </Group>
                )}
              </Stack>
            </ScrollArea.Autosize>
          </Stack>
        </Popover.Dropdown>
      </Popover>
    </Input.Wrapper>
  );
}
