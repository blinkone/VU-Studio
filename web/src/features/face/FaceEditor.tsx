import { Text } from '@mantine/core';
import type { DialPage } from '@/api/types';

export interface FaceEditorProps {
  /** Slot the page belongs to (used for live preview context). */
  slot: number;
  /** The page being edited; only `page.face` is changed by this editor. */
  page: DialPage;
  /** Called with the new face configuration. */
  onChange: (face: DialPage['face']) => void;
}

// Placeholder — the face editor (theme gallery + visual layer editor) is implemented by a feature agent.
export function FaceEditor({ page }: FaceEditorProps) {
  return <Text c="dimmed">Face editor — theme {page.face.theme}</Text>;
}
