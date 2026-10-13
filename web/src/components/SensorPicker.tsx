import { useMemo } from 'react';
import { Group, Select, Text, type SelectProps } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { useSensors } from '@/api/queries';
import type { SensorDto } from '@/api/types';
import { formatValue } from '@/lib/format';

interface SensorPickerProps extends Omit<SelectProps, 'data' | 'value' | 'onChange'> {
  value: string | undefined;
  onChange: (sensorId: string | undefined) => void;
  /** Restricts the choices, e.g. to numeric or color sensors. */
  sensorFilter?: (sensor: SensorDto) => boolean;
}

/** Searchable sensor selector grouped by host/device, showing the current value of each sensor. */
export function SensorPicker({ value, onChange, sensorFilter, ...props }: SensorPickerProps) {
  const { t, i18n } = useTranslation();
  const { data: sensors = [] } = useSensors();

  const byId = useMemo(() => new Map(sensors.map((s) => [s.id, s])), [sensors]);

  const groups = useMemo(() => {
    const map = new Map<string, { value: string; label: string }[]>();
    for (const s of sensors) {
      if (sensorFilter && !sensorFilter(s)) continue;
      const group = [s.host, s.device ?? t(`categories.${s.category}`)].filter(Boolean).join(' · ');
      if (!map.has(group)) map.set(group, []);
      map.get(group)!.push({ value: s.id, label: s.name });
    }
    // Keep a value that is no longer in the catalog selectable, so it is not silently lost.
    if (value && !byId.has(value)) map.set('?', [{ value, label: value }]);
    return [...map.entries()]
      .sort(([a], [b]) => a.localeCompare(b))
      .map(([group, items]) => ({ group, items: items.sort((a, b) => a.label.localeCompare(b.label)) }));
  }, [sensors, sensorFilter, value, byId, t]);

  return (
    <Select
      searchable
      clearable
      limit={300}
      nothingFoundMessage={t('empty')}
      maxDropdownHeight={360}
      {...props}
      data={groups}
      value={value ?? null}
      onChange={(v) => onChange(v ?? undefined)}
      renderOption={({ option }) => {
        const s = byId.get(option.value);
        return (
          <Group justify="space-between" wrap="nowrap" w="100%" gap="xs">
            <Text size="sm" truncate>
              {option.label}
            </Text>
            {s && (
              <Text size="xs" c="dimmed" ff="monospace" style={{ whiteSpace: 'nowrap' }}>
                {s.valueKind === 'number' ? formatValue(s.value, s.unit, i18n.language) : (s.text ?? '–')}
              </Text>
            )}
          </Group>
        );
      }}
    />
  );
}
