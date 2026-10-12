import {
  ActionIcon,
  Button,
  Group,
  NumberInput,
  PasswordInput,
  Select,
  Stack,
  Switch,
  Table,
  TagsInput,
  Text,
  Textarea,
  TextInput,
} from '@mantine/core';
import { useTranslation } from 'react-i18next';
import { IconPlus, IconTrash } from '@tabler/icons-react';
import type { SettingField } from '@/api/types';
import { localized } from '@/i18n';

export const SECRET_PLACEHOLDER = '__secret__';

type Values = Record<string, unknown>;

interface SettingsFormProps {
  fields: SettingField[];
  value: Values;
  onChange: (next: Values) => void;
  disabled?: boolean;
}

/**
 * Renders a form from a provider/theme settings schema (SettingField[] from the service).
 * Missing values fall back to each field's default.
 */
export function SettingsForm({ fields, value, onChange, disabled }: SettingsFormProps) {
  const set = (key: string, v: unknown) => onChange({ ...value, [key]: v });
  return (
    <Stack gap="sm">
      {fields.map((field) => (
        <FieldInput
          key={field.key}
          field={field}
          value={value[field.key] ?? field.default}
          onChange={(v) => set(field.key, v)}
          disabled={disabled}
        />
      ))}
    </Stack>
  );
}

interface FieldInputProps {
  field: SettingField;
  value: unknown;
  onChange: (value: unknown) => void;
  disabled?: boolean;
  compact?: boolean;
}

function FieldInput({ field, value, onChange, disabled, compact }: FieldInputProps) {
  const { t, i18n } = useTranslation();
  const label = compact ? undefined : localized(field.label, i18n.language);
  const description = compact ? undefined : localized(field.help, i18n.language) || undefined;
  const common = {
    label,
    description,
    required: field.required,
    disabled,
    placeholder: field.placeholder,
    'aria-label': localized(field.label, i18n.language),
  };

  switch (field.kind) {
    case 'boolean':
      return (
        <Switch
          label={compact ? undefined : label}
          description={description}
          disabled={disabled}
          checked={value === true || value === 'true'}
          onChange={(e) => onChange(e.currentTarget.checked)}
          aria-label={common['aria-label']}
        />
      );
    case 'number':
    case 'seconds':
      return (
        <NumberInput
          {...common}
          min={field.min}
          max={field.max}
          suffix={field.kind === 'seconds' ? ' s' : undefined}
          value={typeof value === 'number' ? value : typeof value === 'string' && value !== '' ? Number(value) : ''}
          onChange={(v) => onChange(v === '' ? undefined : Number(v))}
        />
      );
    case 'secret':
      return (
        <PasswordInput
          {...common}
          placeholder={value === SECRET_PLACEHOLDER ? '••••••••' : field.placeholder}
          value={value === SECRET_PLACEHOLDER ? '' : String(value ?? '')}
          onChange={(e) => onChange(e.currentTarget.value === '' && value === SECRET_PLACEHOLDER ? SECRET_PLACEHOLDER : e.currentTarget.value)}
        />
      );
    case 'select':
      return (
        <Select
          {...common}
          allowDeselect={!field.required}
          data={(field.options ?? []).map((o) => ({ value: o.value, label: localized(o.label, i18n.language) }))}
          value={value === undefined || value === null ? null : String(value)}
          onChange={(v) => onChange(v ?? undefined)}
        />
      );
    case 'textList':
      return (
        <TagsInput
          {...common}
          value={Array.isArray(value) ? value.map(String) : typeof value === 'string' && value ? value.split(/[\n,;]/).map((s) => s.trim()) : []}
          onChange={onChange}
          clearable
        />
      );
    case 'multiline':
      return (
        <Textarea
          {...common}
          autosize
          minRows={3}
          maxRows={14}
          styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
          value={String(value ?? '')}
          onChange={(e) => onChange(e.currentTarget.value)}
        />
      );
    case 'table':
      return <TableInput field={field} value={value} onChange={onChange} disabled={disabled} label={label} description={description} addLabel={t('actions.add')} />;
    default:
      return (
        <TextInput
          {...common}
          type={field.kind === 'url' ? 'url' : 'text'}
          value={String(value ?? '')}
          onChange={(e) => onChange(e.currentTarget.value)}
        />
      );
  }
}

interface TableInputProps {
  field: SettingField;
  value: unknown;
  onChange: (value: unknown) => void;
  disabled?: boolean;
  label?: string;
  description?: string;
  addLabel: string;
}

function TableInput({ field, value, onChange, disabled, label, description, addLabel }: TableInputProps) {
  const { i18n } = useTranslation();
  const rows = Array.isArray(value) ? (value as Values[]) : [];
  const columns = field.columns ?? [];
  const setRow = (index: number, row: Values) => onChange(rows.map((r, i) => (i === index ? row : r)));
  const addRow = () =>
    onChange([...rows, Object.fromEntries(columns.filter((c) => c.default !== undefined).map((c) => [c.key, c.default]))]);

  return (
    <Stack gap={4}>
      {label && (
        <Text size="sm" fw={500}>
          {label}
          {field.required && <Text span c="red"> *</Text>}
        </Text>
      )}
      {description && (
        <Text size="xs" c="dimmed">
          {description}
        </Text>
      )}
      <Table.ScrollContainer minWidth={Math.max(400, columns.length * 140)}>
        <Table verticalSpacing={4} horizontalSpacing={6} withRowBorders={false}>
          <Table.Thead>
            <Table.Tr>
              {columns.map((c) => (
                <Table.Th key={c.key}>
                  <Text size="xs" c="dimmed" fw={600}>
                    {localized(c.label, i18n.language)}
                  </Text>
                </Table.Th>
              ))}
              <Table.Th w={36} />
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {rows.map((row, index) => (
              <Table.Tr key={index}>
                {columns.map((c) => (
                  <Table.Td key={c.key} miw={c.kind === 'boolean' ? 50 : 110}>
                    <FieldInput
                      compact
                      field={c}
                      value={row[c.key] ?? c.default}
                      disabled={disabled}
                      onChange={(v) => setRow(index, { ...row, [c.key]: v })}
                    />
                  </Table.Td>
                ))}
                <Table.Td>
                  <ActionIcon variant="subtle" color="red" disabled={disabled} onClick={() => onChange(rows.filter((_, i) => i !== index))} aria-label="remove">
                    <IconTrash size={16} />
                  </ActionIcon>
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
      <Group>
        <Button size="compact-sm" variant="light" leftSection={<IconPlus size={14} />} onClick={addRow} disabled={disabled}>
          {addLabel}
        </Button>
      </Group>
    </Stack>
  );
}
