import { useMemo, type CSSProperties } from 'react';
import { Alert, Badge, Grid, Group, Paper, ScrollArea, SegmentedControl, Select, Stack, Text, Title, Tooltip } from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconEye, IconInfoCircle, IconLivePhoto } from '@tabler/icons-react';
import { useDialState } from '@/api/hooks';
import { useLive } from '@/api/live';
import { useSensors } from '@/api/queries';
import { DialPreview } from '@/features/dial/DialPreview';
import { formatReading } from '@/features/dial/format';
import { pageLabel } from './pageLabels';
import type { DialEditorModel } from './useDialEditor';
import { useFacePreview, usePageLightPreview, type PageReading } from './usePagePreview';
import classes from './editor.module.css';

/**
 * Top of the dial editor: the dial itself (live, or a preview of the edited page when it is not on
 * the dial or has unsaved changes), the profile being edited and the page selector.
 */
export function EditorHeader({ model, reading }: { model: DialEditorModel; reading: PageReading }) {
  const { t, i18n } = useTranslation('editor');
  const dial = useDialState(model.slot);
  const activeProfileId = useLive((s) => s.state?.engine.activeProfileId);
  const { data: sensors } = useSensors();
  const { page, profile, assignment } = model;

  const savedPage = model.base?.profiles.find((p) => p.id === profile.id)?.dials.find((d) => d.slot === model.slot)?.pages.find((p) => p.id === page?.id);
  const unsaved = !!page && JSON.stringify(savedPage) !== JSON.stringify(page);
  const onDial = !!page && dial?.profileId === profile.id && dial?.pageId === page.id;
  const previewMode = !!page && (!onDial || unsaved);

  const faceUrl = useFacePreview(model.slot, page, previewMode);
  const previewColor = usePageLightPreview(page, reading);
  const previewTitle = page
    ? page.face.title || page.value.label || reading.sensor?.name || page.name || t('common:slot', { slot: model.slot })
    : undefined;
  const glow = previewMode ? previewColor : dial && dial.previewColor !== '#000000' ? dial.previewColor : null;

  const profileOptions = useMemo(
    () =>
      model.config.profiles.map((p) => ({
        value: p.id,
        label: p.name,
      })),
    [model.config.profiles],
  );
  const tagsFor = (id: string) => {
    const p = model.config.profiles.find((x) => x.id === id);
    const tags: string[] = [];
    if (id === activeProfileId) tags.push(t('header.active'));
    if (id === model.defaultProfile?.id) tags.push(t('header.default'));
    if (p && !p.enabled) tags.push(t('header.disabled'));
    if (p && !p.dials.some((d) => d.slot === model.slot && d.pages.length > 0)) tags.push(t('header.unassigned'));
    return tags;
  };

  return (
    <Paper withBorder p={{ base: 'md', sm: 'lg' }} className={classes.header} style={{ '--editor-glow': glow ? `${glow}26` : 'transparent' } as CSSProperties}>
      <Grid gutter={{ base: 'lg', md: 'xl' }} align="center">
        <Grid.Col span={{ base: 12, sm: 6, md: 5, lg: 4 }}>
          <div className={classes.previewColumn}>
            <Tooltip label={previewMode ? t('header.previewHint') : t('header.liveHint')}>
              <Badge
                className={classes.modeBadge}
                variant="light"
                color={previewMode ? 'amber' : 'teal'}
                leftSection={previewMode ? <IconEye size={12} /> : <IconLivePhoto size={12} />}
              >
                {previewMode ? (unsaved ? t('header.previewUnsaved') : t('header.preview')) : t('header.live')}
              </Badge>
            </Tooltip>
            {previewMode ? (
              <DialPreview
                slot={model.slot}
                size="lg"
                ambient
                faceUrl={faceUrl}
                needle={reading.fraction * 100}
                color={previewColor}
                title={previewTitle}
                valueText={formatReading(reading.display, reading.range.unit, reading.text, i18n.language, page?.value.decimals)}
              />
            ) : (
              <DialPreview slot={model.slot} size="lg" ambient />
            )}
          </div>
        </Grid.Col>

        <Grid.Col span={{ base: 12, sm: 6, md: 7, lg: 8 }}>
          <Stack gap="md">
            <div>
              <Text size="xs" fw={700} tt="uppercase" c="dimmed" style={{ letterSpacing: '0.08em' }}>
                {t('common:slot', { slot: model.slot })}
                {dial?.name ? ` · ${dial.name}` : ''}
              </Text>
              <Title order={1} size="h2" lineClamp={2}>
                {page ? pageLabel(page, model.pageIndex, sensors, t) : t('header.titleEmpty')}
              </Title>
            </div>

            <Select
              label={t('header.profile')}
              description={t('header.profileHint')}
              inputWrapperOrder={['label', 'input', 'description']}
              data={profileOptions}
              value={profile.id}
              onChange={(v) => v && model.setProfile(v)}
              allowDeselect={false}
              maw={420}
              renderOption={({ option }) => (
                <Group justify="space-between" wrap="nowrap" w="100%" gap="xs">
                  <Text size="sm" truncate>
                    {option.label}
                  </Text>
                  <Group gap={4} wrap="nowrap">
                    {tagsFor(option.value).map((tag) => (
                      <Badge key={tag} size="xs" variant="light" color="gray">
                        {tag}
                      </Badge>
                    ))}
                  </Group>
                </Group>
              )}
            />

            {!model.isActiveProfile && model.activeProfileName && (
              <Alert variant="light" color="blue" icon={<IconInfoCircle size={18} />} p="xs">
                <Text size="sm">{t('header.notActive', { name: model.activeProfileName })}</Text>
              </Alert>
            )}

            {assignment && assignment.pages.length > 1 && (
              <div>
                <Text size="sm" fw={500} mb={6} id="page-selector-label">
                  {t('header.page')}
                </Text>
                <ScrollArea type="auto" scrollbarSize={6} offsetScrollbars="x">
                  <SegmentedControl
                    aria-labelledby="page-selector-label"
                    value={page?.id ?? ''}
                    onChange={model.setPage}
                    data={assignment.pages.map((p, i) => ({
                      value: p.id,
                      label: (
                        <Group gap={6} wrap="nowrap">
                          <span>{pageLabel(p, i, sensors, t)}</span>
                          {dial?.profileId === profile.id && dial?.pageId === p.id && (
                            <span
                              style={{ width: 6, height: 6, borderRadius: 3, background: 'var(--mantine-color-teal-5)' }}
                              aria-label={t('header.onDial')}
                              role="img"
                            />
                          )}
                        </Group>
                      ),
                    }))}
                  />
                </ScrollArea>
              </div>
            )}
          </Stack>
        </Grid.Col>
      </Grid>
    </Paper>
  );
}
