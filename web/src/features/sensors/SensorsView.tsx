import { useCallback, useEffect, useMemo, useState } from 'react';
import { ActionIcon, Alert, Badge, Button, Chip, CloseButton, Group, Loader, SegmentedControl, Skeleton, Stack, Switch, Text, TextInput, Title, Tooltip } from '@mantine/core';
import { useDebouncedValue } from '@mantine/hooks';
import { IconAlertCircle, IconChevronsDown, IconChevronsUp, IconPlugConnected, IconSearch, IconWifiOff } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { Link, useSearchParams } from 'react-router';
import { useLiveSubscription } from '@/api/hooks';
import { useLive } from '@/api/live';
import { useSensors } from '@/api/queries';
import { useConfigDraft } from '@/state/configDraft';
import { buildEntries, buildItems, filterBase, normalize, type GroupMode } from './model';
import { SensorDrawer } from './SensorDrawer';
import { SensorTable } from './SensorTable';
import { useLabels } from './useLabels';
import classes from './sensors.module.css';

/** Per-viewer preferences live in this browser only. */
function useStored<T extends string | boolean>(key: string, initial: T): [T, (value: T) => void] {
  const [value, setValue] = useState<T>(() => {
    try {
      const raw = localStorage.getItem(key);
      return raw === null ? initial : (JSON.parse(raw) as T);
    } catch {
      return initial;
    }
  });
  const set = useCallback(
    (next: T) => {
      setValue(next);
      try {
        localStorage.setItem(key, JSON.stringify(next));
      } catch {
        // Not remembered, still works.
      }
    },
    [key],
  );
  return [value, set];
}

export function SensorsView() {
  const { t, i18n } = useTranslation('sensors');
  const locale = i18n.language;
  const sensors = useSensors();
  const labels = useLabels();
  const liveStatus = useLive((s) => s.status);
  const hasProviders = useConfigDraft((s) => (s.draft?.providers.length ?? 1) > 0);
  const [params, setParams] = useSearchParams();
  const provider = params.get('provider');

  // Values of every sensor, 2 Hz. Rows pick their own value from the store.
  useLiveSubscription('*');

  // Staleness is decided by the service; the fetched flag is refreshed regularly so it never gets old.
  const { refetch } = sensors;
  useEffect(() => {
    const timer = setInterval(() => void refetch(), 10_000);
    return () => clearInterval(timer);
  }, [refetch]);

  const [mode, setMode] = useStored<GroupMode>('vustudio.sensors.group', 'device');
  const [hideStale, setHideStale] = useStored<boolean>('vustudio.sensors.hideStale', false);
  const [query, setQuery] = useState('');
  const [debounced] = useDebouncedValue(query, 150);
  const [categories, setCategories] = useState<string[]>([]);
  const [collapsed, setCollapsed] = useState<ReadonlySet<string>>(new Set());

  const [openId, setOpenId] = useState<string | null>(null);
  const [drawerOpen, setDrawerOpen] = useState(false);

  const entries = useMemo(() => buildEntries(sensors.data ?? [], labels), [sensors.data, labels]);
  const base = useMemo(() => filterBase(entries, { query: debounced, categories: new Set(), hideStale, provider }), [entries, debounced, hideStale, provider]);

  const categoryCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const { sensor } of base) counts.set(sensor.category, (counts.get(sensor.category) ?? 0) + 1);
    return counts;
  }, [base]);
  // Categories that exist at all keep their chip even when the search leaves nothing in them.
  const allCategories = useMemo(() => [...new Set(entries.map((e) => e.sensor.category))], [entries]);

  const visible = useMemo(() => (categories.length === 0 ? base : base.filter((e) => categories.includes(e.sensor.category))), [base, categories]);
  const searching = debounced.trim() !== '';
  const items = useMemo(() => buildItems(visible, mode, labels, collapsed, locale, searching), [visible, mode, labels, collapsed, locale, searching]);
  const highlight = useMemo(() => normalize(debounced).split(/\s+/).filter(Boolean), [debounced]);

  const groupKeys = useMemo(() => items.filter((i) => i.type === 'group').map((i) => (i.type === 'group' ? i.key : '')), [items]);
  const toggleGroup = useCallback(
    (key: string) =>
      setCollapsed((previous) => {
        const next = new Set(previous);
        if (!next.delete(key)) next.add(key);
        return next;
      }),
    [],
  );

  const total = sensors.data?.length ?? 0;
  const filtering = searching || categories.length > 0 || hideStale || !!provider;
  const resetFilters = () => {
    setQuery('');
    setCategories([]);
    setHideStale(false);
    if (provider) setParams({}, { replace: true });
  };

  const selected = useMemo(() => (openId ? sensors.data?.find((s) => s.id === openId) : undefined), [openId, sensors.data]);
  const open = useCallback((id: string) => {
    setOpenId(id);
    setDrawerOpen(true);
  }, []);

  return (
    <Stack gap="md" pb={96}>
      <Group justify="space-between" align="flex-start">
        <div>
          <Group gap="xs">
            <Title order={2}>{t('title')}</Title>
            {total > 0 && (
              <Badge variant="light" color="gray" size="lg">
                {total.toLocaleString(locale)}
              </Badge>
            )}
          </Group>
          <Text c="dimmed" maw={720}>
            {t('subtitle')}
          </Text>
        </div>
        {liveStatus !== 'open' && (
          <Badge variant="light" color="yellow" leftSection={<IconWifiOff size={13} aria-hidden />}>
            {t('notLive')}
          </Badge>
        )}
      </Group>

      <Stack gap="sm">
        <Group gap="md" align="flex-end">
          <TextInput
            style={{ flex: '1 1 260px', maxWidth: 460 }}
            aria-label={t('search.label')}
            placeholder={t('search.placeholder')}
            leftSection={<IconSearch size={16} />}
            rightSection={query ? <CloseButton size="sm" aria-label={t('search.clear')} onClick={() => setQuery('')} /> : null}
            value={query}
            onChange={(event) => setQuery(event.currentTarget.value)}
          />
          <SegmentedControl
            aria-label={t('group.label')}
            value={mode}
            onChange={(value) => setMode(value === 'category' ? 'category' : 'device')}
            data={[
              { value: 'device', label: t('group.device') },
              { value: 'category', label: t('group.category') },
            ]}
          />
          <Switch checked={hideStale} onChange={(event) => setHideStale(event.currentTarget.checked)} label={t('hideStale')} />
          <Group gap={4} ml="auto">
            <Tooltip label={t('group.collapseAll')}>
              <ActionIcon variant="default" size="lg" aria-label={t('group.collapseAll')} onClick={() => setCollapsed(new Set(groupKeys))}>
                <IconChevronsUp size={18} />
              </ActionIcon>
            </Tooltip>
            <Tooltip label={t('group.expandAll')}>
              <ActionIcon variant="default" size="lg" aria-label={t('group.expandAll')} onClick={() => setCollapsed(new Set())}>
                <IconChevronsDown size={18} />
              </ActionIcon>
            </Tooltip>
          </Group>
        </Group>

        {allCategories.length > 1 && (
          <Chip.Group multiple value={categories} onChange={setCategories}>
            <div className={classes.chips} role="group" aria-label={t('categoriesLabel')}>
              {[...allCategories]
                .sort((a, b) => labels.category(a).localeCompare(labels.category(b), locale))
                .map((category) => (
                  <Chip key={category} value={category} size="xs" variant="outline" disabled={!categoryCounts.get(category) && !categories.includes(category)}>
                    {labels.category(category)} · {categoryCounts.get(category) ?? 0}
                  </Chip>
                ))}
            </div>
          </Chip.Group>
        )}

        {provider && (
          <Group gap="xs">
            <Badge variant="light" size="lg" rightSection={<CloseButton size="xs" aria-label={t('provider.clear')} onClick={() => setParams({}, { replace: true })} />}>
              {t('provider.only', { name: labels.provider(provider) })}
            </Badge>
          </Group>
        )}
      </Stack>

      {sensors.isError ? (
        <Alert color="red" variant="light" icon={<IconAlertCircle size={18} />} title={t('error.title')}>
          <Group justify="space-between" wrap="nowrap">
            <Text size="sm">{t('error.body')}</Text>
            <Button size="xs" variant="default" onClick={() => void sensors.refetch()}>
              {t('error.retry')}
            </Button>
          </Group>
        </Alert>
      ) : sensors.isLoading ? (
        <Stack gap={0}>
          {Array.from({ length: 8 }, (_, i) => (
            <Skeleton key={i} height={52} mb={4} radius="md" />
          ))}
        </Stack>
      ) : total === 0 ? (
        <Stack align="center" gap="xs" py={48} ta="center" style={{ border: '1px dashed var(--mantine-color-default-border)', borderRadius: 'var(--mantine-radius-lg)' }}>
          <IconPlugConnected size={34} stroke={1.4} color="var(--mantine-color-dimmed)" aria-hidden />
          <Text fw={650}>{t('empty.title')}</Text>
          <Text size="sm" c="dimmed" maw={460} px="md">
            {hasProviders ? t('empty.waiting') : t('empty.noSources')}
          </Text>
          <Button component={Link} to="/sources" variant="light" size="xs">
            {t('empty.goToSources')}
          </Button>
        </Stack>
      ) : visible.length === 0 ? (
        <Stack align="center" gap="xs" py={48} ta="center">
          <Text fw={650}>{t('noMatch.title')}</Text>
          <Text size="sm" c="dimmed">
            {t('noMatch.body')}
          </Text>
          {filtering && (
            <Button variant="light" size="xs" onClick={resetFilters}>
              {t('noMatch.reset')}
            </Button>
          )}
        </Stack>
      ) : (
        <>
          <Group gap="xs" c="dimmed" aria-live="polite">
            <Text size="sm">{filtering ? t('showingOf', { shown: visible.length.toLocaleString(locale), total: total.toLocaleString(locale) }) : t('count', { count: total })}</Text>
            {sensors.isFetching && !sensors.isLoading && <Loader size={12} color="gray" />}
          </Group>
          <SensorTable items={items} locale={locale} highlight={highlight} onOpen={open} onToggleGroup={toggleGroup} />
        </>
      )}

      <SensorDrawer sensor={selected} opened={drawerOpen} onClose={() => setDrawerOpen(false)} onExited={() => setOpenId(null)} labels={labels} locale={locale} />
    </Stack>
  );
}
