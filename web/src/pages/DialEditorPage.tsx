import { Anchor, Center, Group, Loader, Paper, Stack, Tabs, Text, Title } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useTranslation } from 'react-i18next';
import { Link, useParams } from 'react-router';
import { IconArrowLeft, IconBulb, IconDatabase, IconLayoutGrid, IconPhoto } from '@tabler/icons-react';
import { FaceEditor } from '@/features/face/FaceEditor';
import { DataTab } from '@/features/editor/DataTab';
import { EditorHeader } from '@/features/editor/EditorHeader';
import { LightTab } from '@/features/editor/LightTab';
import { NoAssignment } from '@/features/editor/NoAssignment';
import { PagesTab } from '@/features/editor/PagesTab';
import { useDialEditor, type DialEditorModel, type EditorTab } from '@/features/editor/useDialEditor';
import { usePageReading } from '@/features/editor/usePagePreview';

/** Route /dials/:slot — everything one dial shows in one profile: data, face, light and pages. */
export function DialEditorPage() {
  const { t } = useTranslation('editor');
  const { slot: slotParam } = useParams();
  const slot = Number(slotParam);
  const valid = Number.isInteger(slot) && slot >= 1 && slot <= 64;

  return (
    <Stack gap="lg" pb={110} maw={1400} mx="auto">
      <Group>
        <Anchor component={Link} to="/" size="sm" c="dimmed">
          <Group gap={6}>
            <IconArrowLeft size={16} />
            {t('back')}
          </Group>
        </Anchor>
      </Group>
      {valid ? (
        <Editor slot={slot} />
      ) : (
        <Paper withBorder p="xl" ta="center">
          <Title order={1} size="h3">
            {t('notFound.title')}
          </Title>
          <Text c="dimmed" mt="xs">
            {t('notFound.text')}
          </Text>
        </Paper>
      )}
    </Stack>
  );
}

function Editor({ slot }: { slot: number }) {
  const { t } = useTranslation('editor');
  const model = useDialEditor(slot);

  if (!model) {
    return (
      <Center py={80}>
        <Stack align="center" gap="sm">
          <Loader color="amber" />
          <Text c="dimmed" size="sm">
            {t('loading')}
          </Text>
        </Stack>
      </Center>
    );
  }

  return <EditorBody model={model} />;
}

function EditorBody({ model }: { model: DialEditorModel }) {
  const { t } = useTranslation('editor');
  const reading = usePageReading(model.page, model.config.general);
  const wide = useMediaQuery('(min-width: 30em)', true);
  const { page } = model;

  return (
    <>
      <EditorHeader model={model} reading={reading} />
      {!model.assignment || !page ? (
        <NoAssignment model={model} />
      ) : (
        <Tabs value={model.tab} onChange={(v) => v && model.setTab(v as EditorTab)} keepMounted={false} variant="pills" radius="xl">
          <Tabs.List mb="lg" grow={!wide} style={{ flexWrap: 'nowrap' }}>
            <Tabs.Tab value="data" leftSection={wide ? <IconDatabase size={16} /> : undefined}>
              {t('tabs.data')}
            </Tabs.Tab>
            <Tabs.Tab value="face" leftSection={wide ? <IconPhoto size={16} /> : undefined}>
              {t('tabs.face')}
            </Tabs.Tab>
            <Tabs.Tab value="light" leftSection={wide ? <IconBulb size={16} /> : undefined}>
              {t('tabs.light')}
            </Tabs.Tab>
            <Tabs.Tab value="pages" leftSection={wide ? <IconLayoutGrid size={16} /> : undefined}>
              {t('tabs.pages')}
              {model.assignment.pages.length > 1 && (
                <Text span size="xs" ml={6} opacity={0.7} inherit>
                  {model.assignment.pages.length}
                </Text>
              )}
            </Tabs.Tab>
          </Tabs.List>

          <Tabs.Panel value="data">
            <DataTab key={page.id} model={model} reading={reading} />
          </Tabs.Panel>
          <Tabs.Panel value="face">
            <FaceEditor
              slot={model.slot}
              page={page}
              onChange={(face) =>
                model.updatePage((p) => {
                  p.face = face;
                })
              }
            />
          </Tabs.Panel>
          <Tabs.Panel value="light">
            <LightTab key={page.id} model={model} reading={reading} />
          </Tabs.Panel>
          <Tabs.Panel value="pages">
            <PagesTab model={model} />
          </Tabs.Panel>
        </Tabs>
      )}
    </>
  );
}
