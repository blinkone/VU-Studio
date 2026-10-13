import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Group,
  Input,
  Modal,
  Radio,
  Slider,
  Stack,
  Stepper,
  Switch,
  Text,
  ThemeIcon,
  Title,
} from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { useTranslation } from 'react-i18next';
import { IconAlertTriangle, IconCheck, IconFocus2, IconInfoCircle } from '@tabler/icons-react';
import { api } from '@/api/client';
import { useConfigDraft } from '@/state/configDraft';
import { errorMessage, runAction } from './actions';
import { useDialRows } from './dials';
import { NumField } from './fields';
import { NeedleDiagram } from './NeedleDiagram';

type Step = 'choose' | 'full' | 'half' | 'done';
type Point = 'full' | 'half';

const STEPS: readonly Step[] = ['choose', 'full', 'half', 'done'];
/** The DAC range is not documented by the hardware vendor; 12 bits is the safe, usual assumption. */
const STANDARD_MAX = 4095;
const EXTENDED_MAX = 65535;
const SEND_DELAY_MS = 110;

const rawUrl = (uid: string) => `/api/hardware/dials/${encodeURIComponent(uid)}/raw`;

/**
 * Sends raw DAC values to a dial while the user drags: debounced, one request at a time, and always
 * the newest value last, so a fast slider never queues up stale positions.
 */
function useRawSender(uid: string | null, onSent: () => void) {
  const pending = useRef<number | null>(null);
  const inFlight = useRef(false);
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const uidRef = useRef(uid);
  uidRef.current = uid;
  const sentRef = useRef(onSent);
  sentRef.current = onSent;
  const [status, setStatus] = useState<{ busy: boolean; error?: string }>({ busy: false });

  const flush = useCallback(async () => {
    if (inFlight.current) return;
    inFlight.current = true;
    try {
      while (pending.current !== null && uidRef.current) {
        const raw = pending.current;
        pending.current = null;
        setStatus({ busy: true });
        try {
          await api.post(rawUrl(uidRef.current), { raw });
          sentRef.current();
          setStatus({ busy: false });
        } catch (error) {
          setStatus({ busy: false, error: errorMessage(error) });
        }
      }
    } finally {
      inFlight.current = false;
    }
  }, []);

  const send = useCallback(
    (raw: number, immediate = false) => {
      pending.current = raw;
      clearTimeout(timer.current);
      if (immediate) void flush();
      else timer.current = setTimeout(() => void flush(), SEND_DELAY_MS);
    },
    [flush],
  );

  useEffect(() => () => clearTimeout(timer.current), []);
  return { send, ...status };
}

interface CalibrationWizardProps {
  /** Start on the full-scale step for this dial instead of asking which dial to calibrate. */
  initialUid?: string;
  onClose: () => void;
}

/** Guided calibration of the 100 % and 50 % needle positions. Mount it to open it. */
export function CalibrationWizard({ initialUid, onClose }: CalibrationWizardProps) {
  const { t } = useTranslation('settings');
  const { t: tc } = useTranslation('common');
  const isMobile = useMediaQuery('(max-width: 36em)');
  const geometry = useConfigDraft((s) => s.draft?.hardware.geometry);
  const rows = useDialRows();
  const online = rows.filter((r) => r.online);

  const [step, setStep] = useState<Step>(initialUid ? 'full' : 'choose');
  const [uid, setUid] = useState<string | null>(initialUid ?? online[0]?.uid ?? null);
  const [extended, setExtended] = useState(false);
  const [raw, setRaw] = useState<Record<Point, number>>({ full: STANDARD_MAX, half: Math.round(STANDARD_MAX / 2) });
  const [saved, setSaved] = useState<Partial<Record<Point, number>>>({});
  const [saving, setSaving] = useState(false);
  const moved = useRef(false);
  const sender = useRawSender(uid, () => {
    moved.current = true;
  });

  const max = extended ? EXTENDED_MAX : STANDARD_MAX;
  const point: Point | null = step === 'full' || step === 'half' ? step : null;
  const dial = rows.find((r) => r.uid === uid);
  const dialName = dial?.name ?? (dial?.slot ? t('dials.slotN', { n: dial.slot }) : (uid ?? ''));

  // Put the needle where the slider says as soon as a calibration step opens. This runs when the
  // step or the dial changes only — slider moves are sent by `setValue`.
  useEffect(() => {
    if (point && uid) sender.send(raw[point], true);
  }, [point, uid]);

  // The needle was driven by raw values; let the engine put it back on the live value afterwards.
  useEffect(
    () => () => {
      if (moved.current) void api.post('/api/engine/refresh').catch(() => undefined);
    },
    [],
  );

  const setValue = (value: number, immediate = false) => {
    if (!point) return;
    const next = Math.min(max, Math.max(0, Math.round(value)));
    setRaw((r) => ({ ...r, [point]: next }));
    sender.send(next, immediate);
  };

  const save = async () => {
    if (!point || !uid) return;
    setSaving(true);
    const value = raw[point];
    const ok = await runAction(
      () => api.post(`/api/hardware/dials/${encodeURIComponent(uid)}/calibrate`, { point, raw: value }),
      { error: t('calibration.saveFailed') },
    );
    setSaving(false);
    if (!ok) return;
    setSaved((s) => ({ ...s, [point]: value }));
    if (point === 'full') {
      // The DAC is linear: half of the full-scale value is a good starting point for the 50 % mark.
      setRaw((r) => ({ ...r, half: Math.round(value / 2) }));
      setStep('half');
    } else {
      setStep('done');
    }
  };

  const stepIndex = STEPS.indexOf(step);
  const titles: Record<Step, string> = {
    choose: t('calibration.steps.choose'),
    full: t('calibration.steps.full'),
    half: t('calibration.steps.half'),
    done: t('calibration.steps.done'),
  };

  return (
    <Modal
      opened
      onClose={onClose}
      size="lg"
      centered
      fullScreen={isMobile}
      closeOnClickOutside={false}
      title={
        <Text fw={650} size="lg">
          {t('calibration.title')}
        </Text>
      }
    >
      <Stack gap="lg">
        <Stepper
          active={stepIndex}
          size="xs"
          allowNextStepsSelect={false}
          styles={{ stepBody: isMobile ? { display: 'none' } : undefined, separator: { marginInline: isMobile ? 4 : 12 } }}
        >
          {STEPS.map((s) => (
            <Stepper.Step key={s} label={titles[s]} />
          ))}
        </Stepper>
        {isMobile && (
          <Text fw={600} size="sm" mt={-8}>
            {t('calibration.stepOf', { n: stepIndex + 1, total: STEPS.length, title: titles[step] })}
          </Text>
        )}

        {step === 'choose' && (
          <Stack gap="md">
            <Text size="sm" c="dimmed">
              {t('calibration.chooseHelp')}
            </Text>
            {online.length === 0 ? (
              <Alert color="yellow" variant="light" icon={<IconAlertTriangle size={18} />}>
                {t('calibration.noDials')}
              </Alert>
            ) : (
              <Radio.Group value={uid ?? ''} onChange={setUid} label={t('calibration.chooseLabel')}>
                <Stack gap="xs" mt="xs">
                  {online.map((row) => (
                    <Radio.Card key={row.uid} value={row.uid} p="sm" radius="md">
                      <Group wrap="nowrap" gap="sm">
                        <Radio.Indicator />
                        <div style={{ minWidth: 0, flex: 1 }}>
                          <Text fw={600} size="sm" truncate>
                            {row.name ?? (row.slot ? t('dials.slotN', { n: row.slot }) : row.uid)}
                          </Text>
                          <Text size="xs" c="dimmed" ff="monospace" truncate>
                            {row.uid}
                          </Text>
                        </div>
                        {row.slot !== undefined && <Badge variant="light">{t('dials.slotN', { n: row.slot })}</Badge>}
                      </Group>
                    </Radio.Card>
                  ))}
                </Stack>
              </Radio.Group>
            )}
            {uid && (
              <Group>
                <Button
                  size="xs"
                  variant="subtle"
                  leftSection={<IconFocus2 size={15} />}
                  onClick={() =>
                    void runAction(() => api.post(`/api/hardware/dials/${encodeURIComponent(uid)}/identify`), {
                      error: t('dials.identifyFailed'),
                    })
                  }
                >
                  {t('dials.identify')}
                </Button>
              </Group>
            )}
            <Group justify="flex-end">
              <Button variant="default" onClick={onClose}>
                {tc('actions.cancel')}
              </Button>
              <Button disabled={!uid} onClick={() => setStep('full')}>
                {t('calibration.next')}
              </Button>
            </Group>
          </Stack>
        )}

        {point && (
          <Stack gap="md">
            <Group align="flex-start" gap="lg" wrap="wrap">
              <div style={{ flex: '1 1 200px', maxWidth: 300 }}>
                {geometry && (
                  <NeedleDiagram
                    geometry={geometry}
                    variant="compact"
                    fraction={point === 'full' ? 1 : 0.5}
                    label={t('calibration.diagram', { percent: point === 'full' ? 100 : 50 })}
                  />
                )}
              </div>
              <Stack gap={6} style={{ flex: '1 1 240px', minWidth: 0 }}>
                <Title order={4}>{titles[point]}</Title>
                <Text size="sm">{t(`calibration.instructions.${point}`, { name: dialName })}</Text>
              </Stack>
            </Group>

            <Input.Wrapper label={t('calibration.rawLabel')} description={t('calibration.rawHelp')}>
              <Stack gap="sm" mt="xs">
                <Slider
                  value={raw[point]}
                  min={0}
                  max={max}
                  step={1}
                  label={(value) => value}
                  thumbLabel={t('calibration.rawLabel')}
                  onChange={(value) => setValue(value)}
                  onChangeEnd={(value) => setValue(value, true)}
                  mb={4}
                />
                <Group gap="xs" justify="space-between" wrap="wrap">
                  <Group gap={4} wrap="nowrap">
                    {[-100, -10, -1].map((delta) => (
                      <Button key={delta} size="compact-sm" variant="default" onClick={() => setValue(raw[point] + delta, true)} aria-label={t('calibration.nudge', { delta })}>
                        {delta}
                      </Button>
                    ))}
                  </Group>
                  <NumField
                    aria-label={t('calibration.rawLabel')}
                    w={120}
                    size="sm"
                    value={raw[point]}
                    min={0}
                    max={max}
                    allowDecimal={false}
                    onCommit={(value) => setValue(value)}
                    styles={{ input: { textAlign: 'center', fontFamily: 'var(--mantine-font-family-monospace)' } }}
                  />
                  <Group gap={4} wrap="nowrap">
                    {[1, 10, 100].map((delta) => (
                      <Button key={delta} size="compact-sm" variant="default" onClick={() => setValue(raw[point] + delta, true)} aria-label={t('calibration.nudge', { delta: `+${delta}` })}>
                        +{delta}
                      </Button>
                    ))}
                  </Group>
                </Group>
              </Stack>
            </Input.Wrapper>

            <Switch
              checked={extended}
              onChange={(event) => {
                const on = event.currentTarget.checked;
                setExtended(on);
                if (!on && raw[point] > STANDARD_MAX) setValue(STANDARD_MAX, true);
              }}
              label={t('calibration.extended')}
              description={t('calibration.extendedHelp', { max: EXTENDED_MAX })}
            />

            <div aria-live="polite">
              {sender.error ? (
                <Alert color="red" variant="light" icon={<IconAlertTriangle size={18} />} p="xs">
                  <Text size="sm">{t('calibration.sendFailed', { message: sender.error })}</Text>
                </Alert>
              ) : (
                <Text size="xs" c="dimmed">
                  {sender.busy ? t('calibration.moving') : t('calibration.live')}
                </Text>
              )}
            </div>

            <Group justify="space-between">
              <Button
                variant="default"
                disabled={saving}
                onClick={() => setStep(point === 'full' ? 'choose' : 'full')}
              >
                {tc('actions.back')}
              </Button>
              <Button loading={saving} onClick={() => void save()}>
                {t(`calibration.save.${point}`)}
              </Button>
            </Group>
          </Stack>
        )}

        {step === 'done' && (
          <Stack gap="md" align="center" py="sm">
            <ThemeIcon size={56} radius="xl" color="teal" variant="light">
              <IconCheck size={32} />
            </ThemeIcon>
            <Title order={3} ta="center">
              {t('calibration.doneTitle')}
            </Title>
            <Text ta="center" size="sm" c="dimmed">
              {t('calibration.doneSummary', { name: dialName, full: saved.full ?? '–', half: saved.half ?? '–' })}
            </Text>
            <Alert color="yellow" variant="light" icon={<IconInfoCircle size={18} />} w="100%">
              <Text size="sm">{t('calibration.storedInDial')}</Text>
            </Alert>
            <Group justify="center" mt="xs">
              <Button
                variant="default"
                onClick={() => {
                  setSaved({});
                  setRaw({ full: STANDARD_MAX, half: Math.round(STANDARD_MAX / 2) });
                  setStep('choose');
                }}
              >
                {t('calibration.another')}
              </Button>
              <Button onClick={onClose}>{t('calibration.finish')}</Button>
            </Group>
          </Stack>
        )}
      </Stack>
    </Modal>
  );
}
