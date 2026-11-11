export type Disk = {
  name: string
  label: string
  totalBytes: number
  freeBytes: number
}

export type ProcessInfo = {
  id: number
  name: string
  workingSetBytes: number
  cpuSeconds: number
}

export type SystemSnapshot = {
  cpuPercent: number
  memoryUsedBytes: number
  memoryTotalBytes: number
  batteryPercent: number | null
  batteryCharging: boolean
  uptime: string
  downloadBytesPerSecond: number
  uploadBytesPerSecond: number
  disks: Disk[]
  topProcesses: ProcessInfo[]
}

export type VendorTelemetry = {
  cpuFanRpm: number | null
  gpuFanRpm: number | null
  cpuTemperature: number | null
  gpuTemperature: number | null
  source: string | null
  error: string | null
}

export type HardwareProfile = {
  manufacturer: string
  product: string
  model: string
  biosVersion: string
  familyId: string
  familyName: string
  panelName: string
  controlKind: string
  controlAvailable: boolean
  detectedInterfaces: string[]
  capabilities: string[]
  detail: string
}

export type GpuControlSnapshot = {
  name: string
  source: string
  available: boolean
  isAdministrator: boolean
  usagePercent: number | null
  temperatureC: number | null
  powerUsageWatts: number | null
  powerLimitWatts: number | null
  powerDefaultWatts: number | null
  powerMinimumWatts: number | null
  powerMaximumWatts: number | null
  powerWritable: boolean
  fanPercent: number | null
  fanWritable: boolean
  coreOffsetMhz: number | null
  memoryOffsetMhz: number | null
  graphicsClockMhz: number | null
  memoryClockMhz: number | null
  error: string | null
}

export type GpuControlResult = {
  ok: boolean
  message: string
  snapshot: GpuControlSnapshot | null
}

export type GpuUnlockStatus = {
  name: string
  pnpDeviceId: string
  vendorId: string
  deviceId: string
  subsystemVendorId: string
  subsystemDeviceId: string
  driverVersion: string
  systemModel: string
  inventorySupported: boolean
  matchedProfileId: string | null
  profileWriteEnabled: boolean
  profileStatus: string
  upstreamVersion: string
  upstreamUrl: string
  requirement: string
  detail: string
}

export type DlssInstallation = {
  id: string
  gamePath: string
  displayName: string
  installedAt: string
  version: string
  proxyName: string
  backupPath: string
}

export type DlssUnlockedStatus = {
  version: string
  downloadUrl: string
  packageSha256: string
  packagePath: string
  packageReady: boolean
  download: DownloadStatus
  busy: boolean
  operation: string
  error: string | null
  gpuSupported: boolean
  installations: DlssInstallation[]
}

export type FeatureCapability = {
  id: string
  name: string
  status: 'supported' | 'limited' | 'unavailable' | 'on-demand'
  detail: string
}

export type PluginPack = {
  id: string
  name: string
  description: string
  pages: string[]
  delivery: 'bundled' | 'on-demand'
  includes: string[]
  supported: boolean
  enabled: boolean
  detail: string
}

export type PluginCatalog = {
  detectedAt: string
  onboardingCompleted: boolean
  capabilities: FeatureCapability[]
  plugins: PluginPack[]
}

export type FileIndexStatus = {
  indexing: boolean
  fileCount: number
  lastIndexed: string | null
  error: string | null
}

export type Overview = {
  system: SystemSnapshot
  vendor: VendorTelemetry
  pointerSpeed: number
  index: FileIndexStatus
  proxy: ProxyStatus
  timestamp: string
}

export type DisplayBrightness = {
  id: string
  name: string
  current: number
  minimum: number
  maximum: number
  transport: string
  isActive: boolean
  supportsHardwareControl: boolean
  writable: boolean
  pnpId: string
  deviceName: string | null
  error: string | null
}

export type LogitechDevice = {
  id: string
  name: string
  productId: number
  batteryPercent: number | null
  charging: boolean
  dpi: number | null
  deviceIndex: number | null
  available: boolean
  writable: boolean
  error: string | null
}

export type ProxyNodeInfo = {
  name: string
  type: string
  supported: boolean
  warning: string | null
}

export type ProxyStatus = {
  running: boolean
  listenAddress: string
  port: number
  mode: string
  selectedNode: string | null
  profilePath: string | null
  profileLoadedAt: string | null
  nodes: ProxyNodeInfo[]
  systemProxyEnabled: boolean
  error: string | null
}

export type ClipboardEntry = {
  id: number
  kind: string
  preview: string
  createdAt: string
  pinned: boolean
  characterCount: number
}

export type FileSearchResult = {
  path: string
  name: string
  directory: string
  size: number
  modified: string
}

export type AudioSession = {
  processId: number
  name: string
  volumePercent: number
  muted: boolean
}

export type DeviceInfo = {
  instanceId: string
  name: string
  class: string
  manufacturer: string
  status: string
  problemCode: number
  service: string
  canToggle: boolean
}

export type BluetoothBatteryInfo = {
  name: string
  address: number
  batteryPercent: number
}

export type StartupItem = {
  id: string
  name: string
  scope: string
  kind: string
  command: string
  enabled: boolean
  canToggle: boolean
}

export type ScheduledTask = {
  path: string
  name: string
  state: string
  lastRun: string
  nextRun: string
  lastResult: number
  enabled: boolean
}

export type HotkeyRegistration = {
  id: string
  label: string
  combo: string
  action: string
  arguments: string
  registered: boolean
  error: string | null
}

export type MacroStep = {
  type: string
  value: string
  delayMs: number
  x: number
  y: number
}

export type MacroDefinition = {
  id: string
  name: string
  enabled: boolean
  steps: MacroStep[]
}

export type CustomHotkey = {
  id: string
  label: string
  combo: string
  action: string
  arguments: string
}

export type AppSettings = {
  schemaVersion: number
  searchRoots: string[]
  proxyProfilePath: string | null
  proxyPort: number
  proxyMode: string
  proxySelectedNode: string | null
  proxyAutoStart: boolean
  proxySystemProxyEnabled: boolean
  selectedLogitechDevice: string | null
  pointerSpeed: number
  clipboardCaptureImages: boolean
  clipboardHistoryLimit: number
  minimizeToTray: boolean
  launchAtLogin: boolean
  mouseMacrosEnabled: boolean
  enabledPlugins: string[]
  pluginOnboardingCompleted: boolean
  performanceOverlayEnabled: boolean
  performanceOverlayLeft: number | null
  performanceOverlayTop: number | null
  performanceOverlayWidth: number
  performanceOverlayHeight: number
  performanceOverlayOpacity: number
  performanceOverlayFpsSource: string
  performanceOverlayMetrics: string[]
  localModelDirectory: string | null
  localModelEngineDirectory: string | null
  localModelPort: number
  localModelArguments: Record<string, string>
  macroBindings: Record<string, string>
  macros: MacroDefinition[]
  hotkeys: {
    clipboard: string
    screenshot: string
    screenshotPin: string
    pinImage: string
    search: string
    openPanel: string
    proxyToggle: string
    macroEditor: string
    launcher: string
    overlay: string
    custom: CustomHotkey[]
  }
}

export type OverlayOptions = {
  processes: string[]
  displays: Array<{ id: string; name: string; deviceName: string | null }>
}

export type InstalledApp = {
  registryPath: string
  name: string
  version: string
  publisher: string
  uninstallString: string
  installLocation: string
  estimatedBytes: number
  installDate: string
  scope: string
  registryView: string
  hasValidInstallLocation: boolean
}

export type AppLeftover = {
  kind: string
  target: string
  description: string
  isDirectory: boolean
  isRegistry: boolean
  directoryPath: string | null
}

export type DiskUsageGroup = {
  name: string
  path: string
  sizeBytes: number
  fileCount: number
}

export type DiskAnalysisStatus = {
  running: boolean
  root: string | null
  totalBytes: number
  fileCount: number
  indexedGroups: number
  currentPath: string | null
  items: DiskUsageGroup[]
  error: string | null
}

export type DriverInfo = {
  deviceName: string
  manufacturer: string
  version: string
  date: string
  infName: string
  deviceId: string
  class: string
}

export type DriverUpdate = {
  title: string
  manufacturer: string
  model: string
  driverDate: string
  rebootRequired: boolean
}

export type WindowsEventItem = {
  time: string
  logName: string
  level: string
  provider: string
  eventId: number
  message: string
}

export type LogProviderGroup = {
  logName: string
  provider: string
  count: number
  errorCount: number
  latest: string
}

export type WindowsLogReport = {
  since: string
  entries: WindowsEventItem[]
  groups: LogProviderGroup[]
  minidumps: string[]
}

export type ExecutableItem = {
  path: string
  name: string
  kind: string
  size: number
  modified: string
  usageCount: number
  pinned: boolean
}

export type LocalModelInfo = {
  path: string
  name: string
  sizeBytes: number
  modified: string
  quantization: string
  estimatedTokensPerSecond: number
  customArguments: string
}

export type LocalModelEngineInfo = {
  directory: string | null
  serverAvailable: boolean
  benchmarkAvailable: boolean
  ownedByLocalDeck: boolean
}

export type DownloadStatus = {
  running: boolean
  fileName: string | null
  receivedBytes: number
  totalBytes: number
  percent: number
  error: string | null
}

export type LocalServerStatus = {
  running: boolean
  processId: number | null
  port: number | null
  error: string | null
}

export type Bootstrap = {
  app: {
    name: string
    version: string
    machine: string
    os: string
  }
  settings: AppSettings
  capabilities: {
    proxy: boolean
    searchIndex: FileIndexStatus
    minimizedLaunch: boolean
  }
}
