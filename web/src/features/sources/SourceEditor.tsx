import { useState } from 'react';
import { Alert, Button, Collapse, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { IconChevronRight, IconLock } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import type { ProviderDescriptor, ProviderInstanceConfig, ProviderTypeDto } from '@/api/types';
import { localized } from '@/i18n';
import { checkId, normalizeIdInput, suggestId } from './ids';
import { ProviderIcon } from './ProviderIcon';
import { SettingsSections } from './SettingsSections';
import { defaultSettings, materializeSettings, missingRequired, type SettingsValues } from './settings';
import classes from './sources.module.css';

/** Values to start a new source with (e.g. an agent found on the network). */
export interface EditorPrefill {
  name?: string;
  settings?: SettingsValues;
}

export type EditorTarget = { mode: 'add'; type: string; prefill?: EditorPrefill } | { mode: 'edit'; id: string };

export interface EditorResult {
  id: string;
  name: string | undefined;
  settings: SettingsValues;
}

interface SourceEditorProps {
  target: EditorTarget | null;
  opened: boolean;
  /** The instances in the current draft. */
  instances: ProviderInstanceConfig[];
  types: ReadonlyMap<string, ProviderTypeDto>;
  /** False when the UI is used from another machine: local-only sources are then read-only. */
  isLocal: boolean;
  onClose: () => void;
  /** Called once the closing animation is over; the owner may forget the target then. */
  onExited: () => void;
  onSubmit: (target: EditorTarget, result: EditorResult) => void;
}

/** Modal to add a source of a given type, or to edit / inspect an existing one. */
export function SourceEditor({ target, opened, instances, types, isLocal, onClose, onExited, onSubmit }: SourceEditorProps) {
  const { t, i18n } = useTranslation('sources');
  const isPhone = useMediaQuery('(max-width: 36em)');

  const instance = target?.mode === 'edit' ? instances.find((i) => i.id === target.id) : undefined;
  const typeId = target ? (target.mode === 'add' ? target.type : instance?.type) : undefined;
  const descriptor = typeId ? types.get(typeId)?.descriptor : undefined;
  const readOnly = !!descriptor?.localConfigOnly && !isLocal;
  const typeName = descriptor ? localized(descriptor.name, i18n.language) : '';
  const instanceName = instance?.name?.trim() || typeName;

  const title = !target || !descriptor ? '' : target.mode === 'add' ? t('editor.addTitle', { name: typeName }) : readOnly ? instanceName : t('editor.editTitle', { name: instanceName });

  return (
    <Modal
      opened={opened}
      onClose={onClose}
      onExitTransitionEnd={onExited}
      fullScreen={isPhone}
      size="lg"
      centered
      radius="lg"
      closeOnClickOutside={false}
      title={
        descriptor && (
          <span style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
            <span className={classes.tile} style={{ width: 36, height: 36 }}>
              <ProviderIcon icon={descriptor.icon} size={20} />
            </span>
            <span style={{ display: 'block', minWidth: 0 }}>
              <span style={{ display: 'block', fontWeight: 650, lineHeight: 1.25 }}>{title}</span>
              {target?.mode === 'edit' && (
                <span className={classes.id} style={{ display: 'block', color: 'var(--mantine-color-dimmed)' }}>
                  {typeName} · {instance?.id}
                </span>
              )}
            </span>
          </span>
        )
      }
    >
      {target && descriptor && (
        <EditorBody
          key={target.mode === 'add' ? `add:${target.type}:${JSON.stringify(target.prefill ?? null)}` : `edit:${target.id}`}
          mode={target.mode}
          descriptor={descriptor}
          instance={instance}
          prefill={target.mode === 'add' ? target.prefill : undefined}
          takenIds={new Set(instances.filter((i) => i.id !== instance?.id).map((i) => i.id))}
          readOnly={readOnly}
          autoFocus={!isPhone}
          onCancel={onClose}
          onSubmit={(result) => onSubmit(target, result)}
        />
      )}
    </Modal>
  );
}

interface EditorBodyProps {
  mode: 'add' | 'edit';
  descriptor: ProviderDescriptor;
  instance: ProviderInstanceConfig | undefined;
  prefill: EditorPrefill | undefined;
  takenIds: ReadonlySet<string>;
  readOnly: boolean;
  autoFocus: boolean;
  onCancel: () => void;
  onSubmit: (result: EditorResult) => void;
}

function EditorBody({ mode, descriptor, instance, prefill, takenIds, readOnly, autoFocus, onCancel, onSubmit }: EditorBodyProps) {
  const { t, i18n } = useTranslation('sources');
  const { t: tc } = useTranslation('common');

  const [name, setName] = useState(instance?.name ?? prefill?.name ?? '');
  const [id, setId] = useState(() => instance?.id ?? suggestId(descriptor.type, takenIds));
  const [settings, setSettings] = useState<SettingsValues>(() =>
    instance ? structuredClone(instance.settings) : { ...defaultSettings(descriptor.settings), ...structuredClone(prefill?.settings ?? {}) },
  );
  const [advancedOpen, setAdvancedOpen] = useState(false);

  const typeName = localized(descriptor.name, i18n.language);
  const description = localized(descriptor.description, i18n.language);
  // Once created, an id never changes: sensor ids, dials and profiles all refer to it.
  const idLocked = mode === 'edit' || descriptor.singleton;
  const idProblem = idLocked ? null : checkId(id, takenIds);
  const missing = missingRequired(descriptor.settings, settings);
  const canSubmit = !readOnly && idProblem === null && missing.length === 0;

  const submit = () => {
    if (!canSubmit) return;
    onSubmit({ id, name: name.trim() || undefined, settings: materializeSettings(descriptor.settings, settings) });
  };

  return (
    <Stack gap="md">
      {description && (
        <Text size="sm" c="dimmed">
          {description}
        </Text>
      )}

      {readOnly && (
        <Alert color="gray" variant="light" icon={<IconLock size={18} />}>
          {t('editor.readOnly')}
        </Alert>
      )}

      <TextInput
        label={t('editor.nameLabel')}
        description={t('editor.nameHelp')}
        placeholder={typeName}
        value={name}
        maxLength={80}
        disabled={readOnly}
        data-autofocus={autoFocus && !readOnly ? true : undefined}
        onChange={(event) => setName(event.currentTarget.value)}
      />

      <SettingsSections fields={descriptor.settings} value={settings} onChange={setSettings} disabled={readOnly} />

      <div>
        <button
          type="button"
          className={classes.advancedToggle}
          aria-expanded={advancedOpen}
          aria-controls="source-advanced"
          onClick={() => setAdvancedOpen((open) => !open)}
        >
          <IconChevronRight size={16} className={classes.chevron} data-open={advancedOpen} aria-hidden />
          {t('editor.advanced')}
        </button>
        <Collapse in={advancedOpen} id="source-advanced">
          <TextInput
            mt="xs"
            label={t('editor.idLabel')}
            description={idLocked ? t(mode === 'edit' ? 'editor.idLockedEdit' : 'editor.idLockedSingleton') : t('editor.idHelp')}
            value={id}
            disabled={idLocked || readOnly}
            spellCheck={false}
            autoCapitalize="none"
            autoCorrect="off"
            maxLength={63}
            styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
            error={idProblem ? t(`editor.idError.${idProblem}`) : undefined}
            onChange={(event) => setId(normalizeIdInput(event.currentTarget.value))}
          />
        </Collapse>
      </div>

      <Group justify="space-between" align="center" wrap="wrap" gap="sm" className={classes.footer}>
        <Text size="xs" c="dimmed" style={{ flex: '1 1 160px' }} role="status">
          {missing.length > 0 && !readOnly
            ? t('editor.missing', { fields: missing.map((field) => localized(field.label, i18n.language)).join(', ') })
            : ''}
        </Text>
        <Group gap="xs" wrap="nowrap">
          <Button variant="default" onClick={onCancel}>
            {readOnly ? tc('actions.close') : tc('actions.cancel')}
          </Button>
          {!readOnly && (
            <Button onClick={submit} disabled={!canSubmit}>
              {mode === 'add' ? t('editor.add') : tc('actions.apply')}
            </Button>
          )}
        </Group>
      </Group>
    </Stack>
  );
}
