import { createTheme, type MantineColorsTuple } from '@mantine/core';

// Warm amber accent, like the glow of an analog VU meter, on a neutral dark UI.
const amber: MantineColorsTuple = [
  '#fff8e6', '#ffefcc', '#ffdd99', '#ffca61', '#ffbb38', '#ffb11f', '#ffab0f', '#e39500', '#ca8400', '#af7000',
];

const dark: MantineColorsTuple = [
  '#e9e9ea', '#c7c8cc', '#a3a5ab', '#7d8088', '#5c5f68', '#3f424a', '#2c2f36', '#1f2127', '#16181d', '#0f1014',
];

export const theme = createTheme({
  primaryColor: 'amber',
  primaryShade: { light: 7, dark: 5 },
  autoContrast: true,
  colors: { amber, dark },
  fontFamily: 'Inter, ui-sans-serif, system-ui, -apple-system, "Segoe UI", Roboto, sans-serif',
  fontFamilyMonospace: '"JetBrains Mono", ui-monospace, SFMono-Regular, Menlo, Consolas, monospace',
  headings: { fontWeight: '650' },
  defaultRadius: 'md',
  cursorType: 'pointer',
  components: {
    Card: { defaultProps: { withBorder: true, radius: 'lg' } },
    Paper: { defaultProps: { radius: 'lg' } },
    Tooltip: { defaultProps: { withArrow: true, openDelay: 300 } },
  },
});
