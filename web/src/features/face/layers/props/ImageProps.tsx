import { useState } from 'react';
import { Group, Stack, Text } from '@mantine/core';
import { Dropzone } from '@mantine/dropzone';
import { notifications } from '@mantine/notifications';
import { IconPhotoPlus, IconUpload, IconX } from '@tabler/icons-react';
import { useQueryClient } from '@tanstack/react-query';
import { useTranslation } from 'react-i18next';
import { api } from '@/api/client';
import { queryKeys, useAssets } from '@/api/queries';
import type { DitherMode, ImageFit, ImageLayer } from '@/api/types';
import { PropGrid, Section, SegmentField, SelectField, SliderField, SwitchField } from '../../components/fields';
import classes from '../../FaceEditor.module.css';
import { BoxFields, type LayerPropsOf } from './shared';

const ACCEPT = ['image/png', 'image/jpeg', 'image/gif', 'image/bmp', 'image/webp'];
const MAX_BYTES = 5 * 1024 * 1024;

export function ImageProps({ layer, patch }: LayerPropsOf<ImageLayer>) {
  const { t } = useTranslation('face');
  const queryClient = useQueryClient();
  const { data: assets = [] } = useAssets();
  const [uploading, setUploading] = useState(false);
  const id = layer.id;

  const upload = async (file: File) => {
    setUploading(true);
    try {
      const form = new FormData();
      form.append('file', file);
      const result = await api.upload<{ id: string }>('/api/assets', form);
      await queryClient.invalidateQueries({ queryKey: queryKeys.assets });
      patch({ assetId: result.id, name: layer.name ?? file.name.replace(/\.[^.]+$/, '') });
    } catch (error) {
      notifications.show({ color: 'red', title: t('image.uploadFailed'), message: error instanceof Error ? error.message : String(error) });
    } finally {
      setUploading(false);
    }
  };

  return (
    <>
      <Section title={t('image.title')}>
        <Dropzone
          onDrop={(files) => files[0] && void upload(files[0])}
          onReject={() => notifications.show({ color: 'red', message: t('image.rejected') })}
          accept={ACCEPT}
          maxSize={MAX_BYTES}
          multiple={false}
          loading={uploading}
          radius="md"
          p="sm"
        >
          <Group justify="center" gap="sm" wrap="nowrap" style={{ pointerEvents: 'none' }}>
            <Dropzone.Accept>
              <IconUpload size={26} stroke={1.5} />
            </Dropzone.Accept>
            <Dropzone.Reject>
              <IconX size={26} stroke={1.5} />
            </Dropzone.Reject>
            <Dropzone.Idle>
              <IconPhotoPlus size={26} stroke={1.5} />
            </Dropzone.Idle>
            <div>
              <Text size="sm" fw={550}>
                {t('image.drop')}
              </Text>
              <Text size="xs" c="dimmed">
                {t('image.dropHint')}
              </Text>
            </div>
          </Group>
        </Dropzone>
        {assets.length > 0 ? (
          <Stack gap={4}>
            <Text size="xs" c="dimmed">
              {t('image.library')}
            </Text>
            <div className={classes.assetGrid}>
              {[...assets]
                .sort((a, b) => b.created.localeCompare(a.created))
                .map((asset) => (
                  <button
                    key={asset.id}
                    type="button"
                    className={classes.assetCell}
                    data-selected={asset.id === layer.assetId || undefined}
                    aria-pressed={asset.id === layer.assetId}
                    title={asset.name ?? asset.id}
                    aria-label={asset.name ?? asset.id}
                    onClick={() => patch({ assetId: asset.id })}
                  >
                    <img src={`/api/assets/${encodeURIComponent(asset.id)}`} alt="" loading="lazy" />
                  </button>
                ))}
            </div>
          </Stack>
        ) : (
          !layer.assetId && (
            <Text size="xs" c="dimmed">
              {t('image.noAssets')}
            </Text>
          )
        )}
      </Section>

      <Section title={t('props.layout')}>
        <BoxFields layer={layer} patch={patch} />
        <SegmentField<ImageFit>
          label={t('image.fit')}
          value={layer.fit}
          onChange={(fit) => patch({ fit })}
          data={(['contain', 'cover', 'stretch', 'none'] as const).map((v) => ({ value: v, label: t(`fits.${v}`) }))}
        />
      </Section>

      <Section title={t('image.processing')}>
        <PropGrid>
          <SelectField<DitherMode>
            label={t('image.dither')}
            value={layer.dither}
            onChange={(dither) => patch({ dither })}
            data={(['floydSteinberg', 'atkinson', 'bayer', 'threshold'] as const).map((v) => ({ value: v, label: t(`dithers.${v}`) }))}
          />
          <div />
        </PropGrid>
        <SliderField label={t('image.threshold')} value={layer.threshold} min={0} max={255} onChange={(threshold) => patch({ threshold }, `${id}:threshold`)} />
        <SliderField label={t('image.brightness')} value={layer.brightness} min={-100} max={100} onChange={(brightness) => patch({ brightness }, `${id}:brightness`)} />
        <SliderField label={t('image.contrast')} value={layer.contrast} min={-100} max={100} onChange={(contrast) => patch({ contrast }, `${id}:contrast`)} />
        <SwitchField label={t('image.invert')} checked={layer.invertImage} onChange={(invertImage) => patch({ invertImage })} />
      </Section>
    </>
  );
}
