import { useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Badge, Button, Card, CloseButton, Group, SimpleGrid, Skeleton, Stack, Text, TextInput, Title } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertCircle, IconPlugConnected, IconSearch } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import { useSearchParams } from 'react-router';
import { useInfo, useProviders, useSensors } from '@/api/queries';
import type { ProviderGroup, ProviderInstanceConfig, ProviderTypeDto } from '@/api/types';
import { localized } from '@/i18n';
import { useConfigDraft } from '@/state/configDraft';
import { DeleteSourceModal } from './DeleteSourceModal';
import { GROUP_ICONS, GROUP_ORDER } from './groups';
import { SourceCard, type PendingState } from './SourceCard';
import { SourceEditor, type EditorResult, type EditorTarget } from './SourceEditor';
import { sameJson } from './settings';
import { TypeCard } from './TypeCard';
import { sourceUsage } from './usage';
import classes from './sources.module.css';

function normalize(text: string): string {
  return text.toLocaleLowerCase().normalize('NFD').replace(/\p{M}/gu, '');
}

export function SourcesView() {
  const { t, i18n } = useTranslation('sources');
  const providers = useProviders();
  const sensors = useSensors();
  const info = useInfo();
  const draft = useConfigDraft((s) => s.draft);
  const base = useConfigDraft((s) => s.base);
  const update = useConfigDraft((s) => s.update);
  const [params, setParams] = useSearchParams();

  // Until /api/info answers, assume the local PC; the service enforces the rule on save anyway.
  const isLocal = info.data?.local !== false;

  const instances = draft?.providers;
  const types = providers.data?.types;
  const typeMap = useMemo(() => new Map((types ?? []).map((type) => [type.descriptor.type, type])), [types]);
  const statusMap = useMemo(() => new Map((providers.data?.status ?? []).map((status) => [status.id, status])), [providers.data?.status]);
  const baseById = useMemo(() => new Map((base?.providers ?? []).map((instance) => [instance.id, instance])), [base]);
  const sensorCounts = useMemo(() => {
    const counts = new Map<string, number>();
    for (const sensor of sensors.data ?? []) counts.set(sensor.providerId, (counts.get(sensor.providerId) ?? 0) + 1);
    return counts;
  }, [sensors.data]);
  const configuredByType = useMemo(() => {
    const counts = new Map<string, number>();
    for (const instance of instances ?? []) counts.set(instance.type, (counts.get(instance.type) ?? 0) + 1);
    return counts;
  }, [instances]);

  // ------------------------------------------------------------------ editor / delete dialogs

  const [target, setTarget] = useState<EditorTarget | null>(null);
  const [editorOpen, setEditorOpen] = useState(false);
  const [deleting, setDeleting] = useState<ProviderInstanceConfig | null>(null);
  const [deleteOpen, setDeleteOpen] = useState(false);

  const openEditor = (next: EditorTarget) => {
    setTarget(next);
    setEditorOpen(true);
  };

  // Deep links: /sources?add=<type> and /sources?edit=<id> open the editor straight away.
  const deepLinkHandled = useRef(false);
  useEffect(() => {
    if (deepLinkHandled.current || !draft || !providers.data) return;
    deepLinkHandled.current = true;
    const edit = params.get('edit');
    const add = params.get('add');
    if (edit && draft.providers.some((instance) => instance.id === edit)) {
      setTarget({ mode: 'edit', id: edit });
      setEditorOpen(true);
    } else if (add && typeMap.get(add)?.supported) {
      setTarget({ mode: 'add', type: add });
      setEditorOpen(true);
    }
    if (edit || add) setParams({}, { replace: true });
  }, [draft, providers.data, params, setParams, typeMap]);

  const submitEditor = (editing: EditorTarget, result: EditorResult) => {
    if (editing.mode === 'add') {
      update((d) => {
        d.providers.push({ id: result.id, type: editing.type, name: result.name, enabled: true, settings: result.settings });
      });
      const name = result.name ?? localized(typeMap.get(editing.type)?.descriptor.name, i18n.language);
      notifications.show({ message: t('notify.added', { name }), color: 'teal' });
    } else {
      update((d) => {
        const instance = d.providers.find((p) => p.id === editing.id);
        if (!instance) return;
        if (result.name) instance.name = result.name;
        else delete instance.name;
        instance.settings = result.settings;
      });
    }
    setEditorOpen(false);
  };

  const toggleEnabled = (id: string, enabled: boolean) =>
    update((d) => {
      const instance = d.providers.find((p) => p.id === id);
      if (instance) instance.enabled = enabled;
    });

  const removeInstance = (id: string) =>
    update((d) => {
      const index = d.providers.findIndex((p) => p.id === id);
      if (index >= 0) d.providers.splice(index, 1);
    });

  const displayName = (instance: ProviderInstanceConfig) =>
    instance.name?.trim() || localized(typeMap.get(instance.type)?.descriptor.name, i18n.language) || instance.type;

  const usage = useMemo(() => (deleting && draft ? sourceUsage(draft.profiles, deleting.id) : null), [deleting, draft]);

  // ------------------------------------------------------------------ gallery

  const [query, setQuery] = useState('');
  const gallery = useMemo(() => {
    const needle = normalize(query.trim());
    const matches = (type: ProviderTypeDto) =>
      needle === '' ||
      normalize(
        [type.descriptor.type, localized(type.descriptor.name, i18n.language), localized(type.descriptor.description, i18n.language)].join(' '),
      ).includes(needle);

    const byGroup = new Map<ProviderGroup, ProviderTypeDto[]>();
    for (const type of types ?? []) {
      if (!matches(type)) continue;
      // A group this UI does not know yet (newer service) still shows up, under "utilities".
      const group = GROUP_ORDER.includes(type.descriptor.group) ? type.descriptor.group : 'utility';
      byGroup.set(group, [...(byGroup.get(group) ?? []), type]);
    }
    const collator = new Intl.Collator(i18n.language);
    return GROUP_ORDER.filter((group) => byGroup.has(group)).map((group) => ({
      group,
      items: byGroup
        .get(group)!
        .sort((a, b) => collator.compare(localized(a.descriptor.name, i18n.language), localized(b.descriptor.name, i18n.language))),
    }));
  }, [types, query, i18n.language]);

  // ------------------------------------------------------------------ render

  const pendingOf = (instance: ProviderInstanceConfig): PendingState => {
    const saved = baseById.get(instance.id);
    return !saved ? 'new' : sameJson(saved, instance) ? null : 'modified';
  };

  return (
    <Stack gap="xl" pb={96}>
      <div>
        <Title order={2}>{t('title')}</Title>
        <Text c="dimmed" maw={720}>
          {t('subtitle')}
        </Text>
      </div>

      {/* ------------------------------------------------------------ your sources */}
      <section aria-labelledby="sources-yours">
        <Stack gap="sm">
          <Group gap="xs">
            <Title order={3} id="sources-yours">
              {t('yours.title')}
            </Title>
            {instances && (
              <Badge variant="light" color="gray" circle={instances.length < 10}>
                {instances.length}
              </Badge>
            )}
          </Group>

          {!instances ? (
            <SimpleGrid cols={{ base: 1, md: 2, xl: 3 }}>
              {[0, 1, 2].map((i) => (
                <Skeleton key={i} height={170} radius="lg" />
              ))}
            </SimpleGrid>
          ) : instances.length === 0 ? (
            <Card padding="xl" style={{ borderStyle: 'dashed' }}>
              <Stack align="center" gap="xs" ta="center">
                <IconPlugConnected size={32} stroke={1.4} color="var(--mantine-color-dimmed)" aria-hidden />
                <Text fw={600}>{t('yours.emptyTitle')}</Text>
                <Text size="sm" c="dimmed" maw={420}>
                  {t('yours.emptyBody')}
                </Text>
              </Stack>
            </Card>
          ) : (
            <SimpleGrid cols={{ base: 1, md: 2, xl: 3 }}>
              {instances.map((instance) => {
                const type = typeMap.get(instance.type);
                return (
                  <SourceCard
                    key={instance.id}
                    instance={instance}
                    type={type}
                    status={statusMap.get(instance.id)}
                    pending={pendingOf(instance)}
                    sensorCount={sensorCounts.get(instance.id) ?? 0}
                    locked={!!type?.descriptor.localConfigOnly && !isLocal}
                    onToggle={(enabled) => toggleEnabled(instance.id, enabled)}
                    onEdit={() => openEditor({ mode: 'edit', id: instance.id })}
                    onDelete={() => {
                      setDeleting(instance);
                      setDeleteOpen(true);
                    }}
                  />
                );
              })}
            </SimpleGrid>
          )}
        </Stack>
      </section>

      {/* ------------------------------------------------------------ add a source */}
      <section aria-labelledby="sources-add">
        <Stack gap="md">
          <Group justify="space-between" align="flex-end" gap="md">
            <div>
              <Title order={3} id="sources-add">
                {t('add.title')}
              </Title>
              <Text size="sm" c="dimmed">
                {t('add.subtitle')}
              </Text>
            </div>
            <TextInput
              w={{ base: '100%', sm: 280 }}
              aria-label={t('add.search')}
              placeholder={t('add.search')}
              leftSection={<IconSearch size={16} />}
              rightSection={query ? <CloseButton size="sm" aria-label={t('add.clearSearch')} onClick={() => setQuery('')} /> : null}
              value={query}
              onChange={(event) => setQuery(event.currentTarget.value)}
            />
          </Group>

          {providers.isError ? (
            <Alert color="red" variant="light" icon={<IconAlertCircle size={18} />} title={t('add.errorTitle')}>
              <Group justify="space-between" wrap="nowrap">
                <Text size="sm">{t('add.errorBody')}</Text>
                <Button size="xs" variant="default" onClick={() => void providers.refetch()}>
                  {t('add.retry')}
                </Button>
              </Group>
            </Alert>
          ) : providers.isLoading ? (
            <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }}>
              {[0, 1, 2].map((i) => (
                <Skeleton key={i} height={150} radius="lg" />
              ))}
            </SimpleGrid>
          ) : gallery.length === 0 ? (
            <Text c="dimmed" ta="center" py="xl">
              {types && types.length > 0 ? t('add.noMatch') : t('add.noTypes')}
            </Text>
          ) : (
            gallery.map(({ group, items }) => {
              const GroupIcon = GROUP_ICONS[group];
              return (
                <section key={group} aria-labelledby={`sources-group-${group}`}>
                  <Stack gap="sm">
                    <div>
                      <div className={classes.groupHeader}>
                        <span className={classes.groupIcon}>
                          <GroupIcon size={16} aria-hidden />
                        </span>
                        <Title order={4} id={`sources-group-${group}`}>
                          {t(`groups.${group}.title`)}
                        </Title>
                      </div>
                      <Text size="sm" c="dimmed" mt={2}>
                        {t(`groups.${group}.description`)}
                      </Text>
                    </div>
                    <SimpleGrid cols={{ base: 1, sm: 2, lg: 3 }}>
                      {items.map((type) => (
                        <TypeCard
                          key={type.descriptor.type}
                          type={type}
                          configured={configuredByType.get(type.descriptor.type) ?? 0}
                          locked={type.descriptor.localConfigOnly && !isLocal}
                          onAdd={() => openEditor({ mode: 'add', type: type.descriptor.type })}
                          onEditExisting={() => {
                            const existing = instances?.find((instance) => instance.type === type.descriptor.type);
                            if (existing) openEditor({ mode: 'edit', id: existing.id });
                          }}
                        />
                      ))}
                    </SimpleGrid>
                  </Stack>
                </section>
              );
            })
          )}
        </Stack>
      </section>

      <SourceEditor
        target={target}
        opened={editorOpen}
        instances={instances ?? []}
        types={typeMap}
        isLocal={isLocal}
        onClose={() => setEditorOpen(false)}
        onExited={() => setTarget(null)}
        onSubmit={submitEditor}
      />

      <DeleteSourceModal
        instance={deleting}
        name={deleting ? displayName(deleting) : ''}
        autoCreated={!!deleting && !!typeMap.get(deleting.type)?.descriptor.autoCreate}
        usage={usage}
        opened={deleteOpen}
        onClose={() => setDeleteOpen(false)}
        onExited={() => setDeleting(null)}
        onConfirm={() => {
          if (deleting) removeInstance(deleting.id);
          setDeleteOpen(false);
        }}
        onDisable={() => {
          if (deleting) toggleEnabled(deleting.id, false);
          setDeleteOpen(false);
        }}
      />
    </Stack>
  );
}
