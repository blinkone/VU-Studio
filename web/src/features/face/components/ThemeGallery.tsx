import { memo, useMemo } from 'react';
import { Alert, Badge, Loader, Skeleton, Stack, Text, ThemeIcon, Title } from '@mantine/core';
import { useInViewport } from '@mantine/hooks';
import { IconCheck, IconPalette, IconPhotoOff, IconPencil } from '@tabler/icons-react';
import { useTranslation } from 'react-i18next';
import type { DialPage, FaceConfig, ThemeInfo } from '@/api/types';
import { localized } from '@/i18n';
import type { PreviewRequest } from '../preview/previewClient';
import { useThemeThumbnail } from '../preview/useThemeThumbnail';
import classes from '../FaceEditor.module.css';

const FAMILY_ORDER = ['vintage', 'minimal', 'instrument', 'hud', 'retro'];

interface ThemeGalleryProps {
  slot: number;
  page: DialPage;
  face: FaceConfig;
  themes: ThemeInfo[] | undefined;
  loading: boolean;
  onSelect: (themeId: string) => void;
}

/** Theme cards grouped by family, each with a real preview of the current page in that theme. */
export function ThemeGallery({ slot, page, face, themes, loading, onSelect }: ThemeGalleryProps) {
  const { t, i18n } = useTranslation('face');
  const custom = face.custom !== undefined;

  const groups = useMemo(() => {
    const map = new Map<string, ThemeInfo[]>();
    for (const theme of themes ?? []) {
      const family = theme.family || theme.id.split('/')[0];
      if (!map.has(family)) map.set(family, []);
      map.get(family)!.push(theme);
    }
    const rank = (f: string) => (FAMILY_ORDER.indexOf(f) < 0 ? FAMILY_ORDER.length : FAMILY_ORDER.indexOf(f));
    return [...map.entries()].sort(([a], [b]) => rank(a) - rank(b) || a.localeCompare(b));
  }, [themes]);

  // Everything except the theme (and the current theme's own options) is shared by all thumbnails.
  const shared = useMemo(
    () =>
      JSON.stringify({
        slot,
        value: page.value,
        title: face.title,
        subtitle: face.subtitle,
        icon: face.icon,
        showIcon: face.showIcon,
        showValue: face.showValue,
        showSparkline: face.showSparkline,
        warnFrom: face.warnFrom,
        labelCount: face.labelCount,
        inverted: face.inverted,
      }),
    [slot, page.value, face.title, face.subtitle, face.icon, face.showIcon, face.showValue, face.showSparkline, face.warnFrom, face.labelCount, face.inverted],
  );

  if (loading) {
    return (
      <div className={classes.themeGrid}>
        {Array.from({ length: 4 }, (_, i) => (
          <Skeleton key={i} h={170} radius="md" />
        ))}
      </div>
    );
  }

  if (groups.length === 0) {
    return (
      <Stack align="center" gap={6} py="xl" px="md" ta="center">
        <ThemeIcon variant="light" size={44} radius="xl" color="gray">
          <IconPalette size={24} />
        </ThemeIcon>
        <Text fw={600}>{t('gallery.emptyTitle')}</Text>
        <Text size="sm" c="dimmed">
          {t('gallery.emptyText')}
        </Text>
        <Badge variant="default" ff="monospace" tt="none">
          {face.theme}
        </Badge>
      </Stack>
    );
  }

  return (
    <Stack gap="lg">
      {custom && (
        <Alert variant="light" icon={<IconPencil size={18} />} p="sm">
          <Text size="sm">{t('gallery.customNotice')}</Text>
        </Alert>
      )}
      {groups.map(([family, list]) => (
        <Stack key={family} gap="xs">
          <Title order={6} className={classes.familyTitle}>
            {t(`families.${family}`, { defaultValue: family.charAt(0).toUpperCase() + family.slice(1) })}
          </Title>
          <div className={classes.themeGrid}>
            {list.map((theme) => (
              <ThemeCard
                key={theme.id}
                theme={theme}
                name={localized(theme.name, i18n.language)}
                description={localized(theme.description, i18n.language)}
                selected={!custom && theme.id === face.theme}
                base={custom && theme.id === face.theme}
                baseLabel={t('gallery.base')}
                selectedLabel={t('gallery.selected')}
                cacheKey={`${theme.id}|${shared}|${theme.id === face.theme ? JSON.stringify(face.options ?? null) : ''}`}
                slot={slot}
                page={page}
                face={face}
                onSelect={onSelect}
              />
            ))}
          </div>
        </Stack>
      ))}
    </Stack>
  );
}

interface ThemeCardProps {
  theme: ThemeInfo;
  name: string;
  description: string;
  selected: boolean;
  base: boolean;
  baseLabel: string;
  selectedLabel: string;
  cacheKey: string;
  slot: number;
  page: DialPage;
  face: FaceConfig;
  onSelect: (themeId: string) => void;
}

const ThemeCard = memo(function ThemeCard(props: ThemeCardProps) {
  const { theme, name, description, selected, base, cacheKey, onSelect } = props;
  const { ref, inViewport } = useInViewport<HTMLButtonElement>();

  // Rebuilt only when the cache key changes, i.e. when something that affects the thumbnail changed.
  const request = useMemo<PreviewRequest>(() => buildRequest(props.slot, props.page, props.face, theme.id), [cacheKey]);
  const thumb = useThemeThumbnail(cacheKey, request, inViewport);

  return (
    <button
      ref={ref}
      type="button"
      className={classes.themeCard}
      data-selected={selected || undefined}
      aria-pressed={selected}
      onClick={() => onSelect(theme.id)}
      title={description || name}
    >
      <div className={classes.thumb}>
        {thumb.url ? (
          <img src={thumb.url} alt="" draggable={false} />
        ) : thumb.status === 'unavailable' ? (
          <div className={classes.thumbEmpty}>
            <IconPhotoOff size={22} stroke={1.5} />
          </div>
        ) : (
          <div className={classes.thumbEmpty}>
            <Loader size="xs" type="dots" color="dark" />
          </div>
        )}
        {selected && (
          <ThemeIcon className={classes.thumbCheck} size={20} radius="xl" aria-label={props.selectedLabel}>
            <IconCheck size={13} stroke={3} />
          </ThemeIcon>
        )}
      </div>
      <div className={classes.themeMeta}>
        <Text size="sm" fw={600} lineClamp={1}>
          {name}
          {base && (
            <Badge size="xs" variant="light" ml={6} style={{ verticalAlign: 'middle' }}>
              {props.baseLabel}
            </Badge>
          )}
        </Text>
        {description && (
          <Text size="xs" c="dimmed" lineClamp={2} lh={1.35}>
            {description}
          </Text>
        )}
      </div>
    </button>
  );
});

function buildRequest(slot: number, page: DialPage, face: FaceConfig, themeId: string): PreviewRequest {
  return {
    slot,
    page: {
      ...page,
      face: { ...face, theme: themeId, custom: undefined, options: themeId === face.theme ? face.options : undefined },
    },
    scale: 2,
  };
}
