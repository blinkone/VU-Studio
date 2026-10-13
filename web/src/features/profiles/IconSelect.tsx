import { useMemo } from 'react';
import { Group, Select, Text, type SelectProps } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconStack2 } from '@tabler/icons-react';
import { useIcons } from '@/api/queries';
import type { IconInfo } from '@/api/types';
import { MdiIcon } from '@/components/MdiIcon';

const prettify = (id: string) => id.replace(/-/g, ' ');

interface IconSelectProps extends Omit<SelectProps, 'data' | 'value' | 'onChange'> {
  value: string | undefined;
  onChange: (iconId: string | undefined) => void;
}

/** Searchable select of the bundled Material Design Icons (the same set the e-paper faces use). */
export function IconSelect({ value, onChange, ...props }: IconSelectProps) {
  const { t } = useTranslation('profiles');
  const { data: icons } = useIcons();

  const { data, byId } = useMemo(() => {
    const list: IconInfo[] = icons ?? [];
    const groups = new Map<string, { value: string; label: string }[]>();
    for (const icon of list) {
      const items = groups.get(icon.category) ?? [];
      items.push({ value: icon.id, label: prettify(icon.id) });
      groups.set(icon.category, items);
    }
    // Keep an icon that is no longer in the catalog selectable, so it is not silently lost.
    if (value && !list.some((i) => i.id === value)) groups.set('?', [{ value, label: prettify(value) }]);
    return {
      byId: new Map(list.map((i) => [i.id, i])),
      data: [...groups.entries()]
        .sort(([a], [b]) => a.localeCompare(b))
        .map(([group, items]) => ({ group, items: items.sort((a, b) => a.label.localeCompare(b.label)) })),
    };
  }, [icons, value]);

  return (
    <Select
      searchable
      clearable
      limit={60}
      maxDropdownHeight={320}
      nothingFoundMessage={t('editor.iconNone')}
      placeholder={data.length === 0 ? t('editor.iconUnavailable') : t('editor.iconPlaceholder')}
      disabled={data.length === 0 && !value}
      {...props}
      data={data}
      value={value ?? null}
      onChange={(next) => onChange(next ?? undefined)}
      leftSection={value ? <MdiIcon id={value} size={18} /> : <IconStack2 size={18} stroke={1.5} />}
      renderOption={({ option }) => (
        <Group gap="sm" wrap="nowrap">
          <svg width={20} height={20} viewBox="0 0 24 24" aria-hidden style={{ flex: 'none' }}>
            <path d={byId.get(option.value)?.path ?? ''} fill="currentColor" />
          </svg>
          <Text size="sm" truncate>
            {option.label}
          </Text>
        </Group>
      )}
    />
  );
}
