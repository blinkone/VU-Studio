// TypeScript mirror of the service's JSON model (src/VuStudio.Core). Keep in sync with docs/api.md.
// JSON is camelCase, enums are camelCase strings, null values are usually omitted.

export type LocalizedText = { en: string; it?: string };

// ---------------------------------------------------------------- sensors & providers

export type SensorCategory =
  | 'cpu' | 'gpu' | 'memory' | 'storage' | 'network' | 'cooling' | 'power' | 'battery' | 'motherboard'
  | 'audio' | 'media' | 'game' | 'process' | 'time' | 'weather' | 'finance' | 'home' | 'system' | 'custom';

export type SensorValueKind = 'number' | 'text' | 'color';

export interface SensorDto {
  id: string;
  name: string;
  device?: string;
  category: SensorCategory;
  kind?: string;
  unit: string;
  valueKind: SensorValueKind;
  min?: number;
  max?: number;
  icon?: string;
  host?: string;
  providerId: string;
  value?: number;
  text?: string;
  updated?: string;
  stale: boolean;
}

/** A live value from the `values` WebSocket message. */
export interface LiveValue {
  id: string;
  v?: number | null;
  t?: string | null;
  s: boolean;
  u?: string | null;
}

export type SettingKind =
  | 'text' | 'number' | 'boolean' | 'secret' | 'select' | 'textList' | 'url' | 'multiline' | 'table' | 'seconds';

export interface SettingOption {
  value: string;
  label: LocalizedText;
}

export interface SettingField {
  key: string;
  label: LocalizedText;
  kind: SettingKind;
  default?: unknown;
  required: boolean;
  help?: LocalizedText;
  placeholder?: string;
  options?: SettingOption[];
  min?: number;
  max?: number;
  columns?: SettingField[];
}

export type ProviderGroup = 'thisPc' | 'software' | 'network' | 'homeAutomation' | 'online' | 'utility';

export interface ProviderDescriptor {
  type: string;
  name: LocalizedText;
  description?: LocalizedText;
  icon: string;
  group: ProviderGroup;
  singleton: boolean;
  autoCreate: boolean;
  /** e.g. "any", "windows", "windows, linux". */
  platforms: string;
  localConfigOnly: boolean;
  runsInUserSession: boolean;
  settings: SettingField[];
}

export interface ProviderTypeDto {
  descriptor: ProviderDescriptor;
  supported: boolean;
}

export type ProviderHealth = 'starting' | 'ok' | 'degraded' | 'unavailable' | 'error' | 'stopped';

export interface ProviderStatusInfo {
  id: string;
  type: string;
  name?: string;
  enabled: boolean;
  health: ProviderHealth;
  message?: string;
  since?: string;
  restarts: number;
}

export interface ProvidersDto {
  types: ProviderTypeDto[];
  status: ProviderStatusInfo[];
}

export interface ProviderInstanceConfig {
  id: string;
  type: string;
  name?: string;
  enabled: boolean;
  settings: Record<string, unknown>;
}

// ---------------------------------------------------------------- configuration

export type NightAction = 'none' | 'dim' | 'lightsOff' | 'sleep';

export interface Easing {
  step: number;
  periodMs: number;
}

export interface NeedleGeometry {
  pivotX: number;
  pivotY: number;
  startAngle: number;
  endAngle: number;
}

export interface GeneralSettings {
  language: 'auto' | 'en' | 'it';
  temperatureUnit: string;
  networkInBits: boolean;
}

export interface DialSettings {
  uid: string;
  slot: number;
  name?: string;
  needleEasing?: Easing;
  backlightEasing?: Easing;
}

export interface HardwareSettings {
  port: string;
  simulate: number;
  dials: DialSettings[];
  geometry: NeedleGeometry;
  masterBrightness: number;
}

export interface WebSettings {
  port: number;
  allowLan: boolean;
  /** "__set__" when a password exists, absent otherwise (changed via /api/auth/password). */
  passwordHash?: string | null;
  apiToken: string;
  allowedHosts: string[];
}

export interface NightSettings {
  onLock: NightAction;
  onIdle: NightAction;
  idleMinutes: number;
  quietHoursEnabled: boolean;
  /** "HH:mm:ss" */
  quietFrom: string;
  quietTo: string;
  quietAction: NightAction;
  dimPercent: number;
  sleepOnShutdown: boolean;
}

export interface HistorySettings {
  enabled: boolean;
  retentionDays: number;
  extraSensors: string[];
}

export interface AppConfig {
  version: number;
  revision: number;
  general: GeneralSettings;
  hardware: HardwareSettings;
  web: WebSettings;
  night: NightSettings;
  history: HistorySettings;
  providers: ProviderInstanceConfig[];
  profiles: Profile[];
}

// ---------------------------------------------------------------- profiles & pages

export type ProfileActivation = 'always' | 'auto' | 'manual';
export type ConditionMatch = 'any' | 'all';
export type OfflineBehavior = 'zero' | 'hold' | 'dim';
export type ScaleType = 'linear' | 'logarithmic' | 'squareRoot' | 'custom';
export type Comparison = 'above' | 'below';
export type DayOfWeek = 'sunday' | 'monday' | 'tuesday' | 'wednesday' | 'thursday' | 'friday' | 'saturday';

export interface ScalePoint {
  value: number;
  position: number;
}

export interface ValueBinding {
  sensorId?: string;
  /** "auto" or a display unit compatible with the sensor's unit (see /api/units/{unit}). */
  displayUnit: string;
  min?: number;
  max?: number;
  scale: ScaleType;
  points?: ScalePoint[];
  invert: boolean;
  attackMs: number;
  releaseMs: number;
  holdMs: number;
  offline: OfflineBehavior;
  decimals?: number;
  label?: string;
  needleEasing?: Easing;
}

export interface DialPage {
  id: string;
  name?: string;
  value: ValueBinding;
  face: FaceConfig;
  light: BacklightConfig;
  durationSeconds?: number;
}

export interface DialAssignment {
  slot: number;
  pages: DialPage[];
  rotationSeconds: number;
}

export type ProfileCondition =
  | { type: 'processRunning'; processes: string[] }
  | { type: 'foregroundApp'; processes: string[] }
  | { type: 'schedule'; days: DayOfWeek[]; from: string; to: string }
  | { type: 'sensor'; sensorId: string; comparison: Comparison; value: number; forSeconds: number };

export interface Profile {
  id: string;
  name: string;
  icon?: string;
  enabled: boolean;
  priority: number;
  activation: ProfileActivation;
  match: ConditionMatch;
  conditions: ProfileCondition[];
  inheritUnassigned: boolean;
  dials: DialAssignment[];
}

// ---------------------------------------------------------------- lighting

export type BacklightMode = 'off' | 'fixed' | 'gradient' | 'thresholds' | 'sync';
export type SyncSource = 'accentColor' | 'albumArt' | 'sensor';
export type LightEffect = 'none' | 'breathe' | 'pulse' | 'blink' | 'rainbow';

/** "#RRGGBB" or "#RRGGBB+WW" (WW = white LED 00..FF). */
export type RgbwHex = string;

export interface GradientStop {
  position: number;
  color: RgbwHex;
}

export interface ThresholdStep {
  value: number;
  color: RgbwHex;
}

export interface AlarmRule {
  enabled: boolean;
  /** null/absent = the page value (display units); otherwise a sensor in canonical units. */
  sensorId?: string;
  comparison: Comparison;
  threshold: number;
  hysteresis: number;
  forSeconds: number;
  color: RgbwHex;
  effect: LightEffect;
  periodMs: number;
  notify: boolean;
  message?: string;
}

export interface BacklightConfig {
  mode: BacklightMode;
  color: RgbwHex;
  gradient: GradientStop[];
  thresholds: ThresholdStep[];
  sync: SyncSource;
  syncSensorId?: string;
  brightness: number;
  effect: LightEffect;
  effectPeriodMs: number;
  effectDepth: number;
  alarms: AlarmRule[];
}

export interface LedLevels {
  r: number;
  g: number;
  b: number;
  w: number;
}

// ---------------------------------------------------------------- faces

export type Ink = 'foreground' | 'background';
export type TextAlign = 'left' | 'center' | 'right';
export type TextBaseline = 'top' | 'middle' | 'baseline' | 'bottom';
export type TextOverflow = 'visible' | 'clip' | 'ellipsis' | 'shrink';
export type DitherMode = 'threshold' | 'floydSteinberg' | 'atkinson' | 'bayer';
export type ImageFit = 'contain' | 'cover' | 'stretch' | 'none';
export type ShapeKind = 'rectangle' | 'ellipse' | 'line' | 'arc';
export type TickStyle = 'line' | 'dot' | 'wedge';
export type ZoneStyle = 'band' | 'hatched' | 'dotted' | 'outline';
export type Orientation = 'horizontal' | 'vertical';
export type SparklineStyle = 'line' | 'area' | 'bars';

interface LayerBase {
  id: string;
  name?: string;
  visible: boolean;
  ink: Ink;
}

export interface ScaleZone {
  from: number;
  to: number;
  style: ZoneStyle;
  innerRadius: number;
  outerRadius: number;
}

export interface ScaleLayer extends LayerBase {
  type: 'scale';
  radius: number;
  majorLength: number;
  minorLength: number;
  majorWidth: number;
  minorWidth: number;
  tickStyle: TickStyle;
  majorCount: number;
  minorPerMajor: number;
  evenSpacing: boolean;
  showLabels: boolean;
  labelRadius: number;
  labelFont: string;
  labelSize: number;
  labelWeight: number;
  labelFormat?: string;
  labelDivisor: number;
  customLabels?: string[];
  rotateLabels: boolean;
  showArc: boolean;
  arcRadius?: number;
  arcWidth: number;
  zones: ScaleZone[];
}

export interface TextLayer extends LayerBase {
  type: 'text';
  x: number;
  y: number;
  text: string;
  font: string;
  size: number;
  weight: number;
  italic: boolean;
  align: TextAlign;
  baseline: TextBaseline;
  maxWidth?: number;
  overflow: TextOverflow;
  letterSpacing: number;
  uppercase: boolean;
  smooth: boolean;
  rotation: number;
}

export interface IconLayer extends LayerBase {
  type: 'icon';
  x: number;
  y: number;
  size: number;
  icon: string;
}

export interface ImageLayer extends LayerBase {
  type: 'image';
  assetId: string;
  x: number;
  y: number;
  width: number;
  height: number;
  fit: ImageFit;
  dither: DitherMode;
  threshold: number;
  brightness: number;
  contrast: number;
  invertImage: boolean;
}

export interface ShapeLayer extends LayerBase {
  type: 'shape';
  kind: ShapeKind;
  x: number;
  y: number;
  width: number;
  height: number;
  x2: number;
  y2: number;
  strokeWidth: number;
  fill: boolean;
  stroke: boolean;
  cornerRadius: number;
  startAngle: number;
  sweepAngle: number;
  dashed: boolean;
}

export interface BarLayer extends LayerBase {
  type: 'bar';
  x: number;
  y: number;
  width: number;
  height: number;
  sensorId?: string;
  min?: number;
  max?: number;
  orientation: Orientation;
  segments: number;
  gap: number;
  border: boolean;
}

export interface SparklineLayer extends LayerBase {
  type: 'sparkline';
  x: number;
  y: number;
  width: number;
  height: number;
  sensorId?: string;
  windowSeconds: number;
  style: SparklineStyle;
  strokeWidth: number;
  min?: number;
  max?: number;
  border: boolean;
}

export type FaceLayer = ScaleLayer | TextLayer | IconLayer | ImageLayer | ShapeLayer | BarLayer | SparklineLayer;

export interface FaceDocument {
  version: number;
  inverted: boolean;
  layers: FaceLayer[];
}

export interface FaceConfig {
  /** "{family}/{variant}" from /api/catalog/themes. */
  theme: string;
  title?: string;
  subtitle?: string;
  icon?: string;
  showIcon: boolean;
  showValue: boolean;
  showSparkline: boolean;
  /** Warning zone start (0..1); null = no zone. Always present in JSON. */
  warnFrom: number | null;
  labelCount: number;
  inverted: boolean;
  options?: Record<string, unknown>;
  /** When set, rendered instead of the theme. */
  custom?: FaceDocument;
  minRefreshSeconds: number;
}

export interface ThemeInfo {
  id: string;
  family: string;
  name: LocalizedText;
  description?: LocalizedText;
  options: SettingField[];
}

export interface FontInfo {
  id: string;
  family: string;
  category: string;
  weights: number[];
  hasItalic: boolean;
}

export interface IconInfo {
  id: string;
  category: string;
  /** SVG path data on a 24×24 viewBox. */
  path: string;
}

export interface AssetInfo {
  id: string;
  name?: string;
  size: number;
  created: string;
}

// ---------------------------------------------------------------- live state

export type HubConnectionState = 'disconnected' | 'connecting' | 'connected' | 'error';

export interface HubStatus {
  state: HubConnectionState;
  port?: string;
  message?: string;
  simulated: boolean;
}

export interface DialInfo {
  uid: string;
  busIndex: number;
  online: boolean;
  firmwareVersion?: string;
  hardwareVersion?: string;
  protocolVersion?: string;
  buildHash?: string;
  needleEasing?: Easing;
  backlightEasing?: Easing;
}

export interface DialStateSnapshot {
  slot: number;
  uid?: string;
  name?: string;
  online: boolean;
  profileId?: string;
  pageIndex: number;
  pageCount: number;
  pageId?: string;
  pageName?: string;
  sensorId?: string;
  value?: number;
  valueText?: string;
  unit: string;
  title: string;
  /** 0..100, smoothed. */
  needle: number;
  light: LedLevels;
  /** Approximate RGB of the backlight, "#RRGGBB". */
  previewColor: string;
  /** Increments whenever the e-paper face changes; reload /api/dials/{slot}/face.png. */
  faceVersion: number;
  assigned: boolean;
  alarmActive: boolean;
}

export interface EngineSnapshot {
  activeProfileId?: string;
  activeProfileName?: string;
  manualProfileId?: string;
  night: NightAction;
  paused: boolean;
  dials: DialStateSnapshot[];
  time: string;
}

export interface StateDto {
  revision: number;
  engine: EngineSnapshot;
  hub: HubStatus;
  hardware: DialInfo[];
  providers: ProviderStatusInfo[];
}

export interface NotificationDto {
  title: string;
  message: string;
  icon?: string;
  slot: number;
}

export interface InfoDto {
  name: string;
  version: string;
  os: string;
  machine: string;
  dataDirectory?: string;
  simulate: number;
  local: boolean;
}

export interface AuthDto {
  local: boolean;
  authenticated: boolean;
  lanEnabled: boolean;
  passwordSet: boolean;
}

export interface HistoryAggregateDto {
  minute: string;
  min: number;
  avg: number;
  max: number;
  count: number;
}

export interface HistoryPointDto {
  time: string;
  value: number;
}

// ---------------------------------------------------------------- discovery

export interface DiscoveredAgentDto {
  name: string;
  host: string;
  port: number;
  addresses: string[];
  version: string | null;
  /** An `agent` source already points at this host and port. */
  alreadyAdded: boolean;
}

export interface DiscoveredAgentsDto {
  agents: DiscoveredAgentDto[];
  /** Set when network discovery could not run (no multicast); the list is then empty. */
  message: string | null;
}
