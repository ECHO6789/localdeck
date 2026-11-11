import { useCallback, useEffect, useRef, useState } from 'react'
import { invoke, onNativeEvent } from './bridge'
import MacroEditor from './components/MacroEditor'
import type {
  AppSettings,
  AppLeftover,
  AudioSession,
  BluetoothBatteryInfo,
  Bootstrap,
  ClipboardEntry,
  DeviceInfo,
  DiskAnalysisStatus,
  DisplayBrightness,
  DownloadStatus,
  DriverInfo,
  DriverUpdate,
  DlssUnlockedStatus,
  ExecutableItem,
  FeatureCapability,
  FileIndexStatus,
  FileSearchResult,
  GpuControlResult,
  GpuControlSnapshot,
  GpuUnlockStatus,
  HardwareProfile,
  HotkeyRegistration,
  InstalledApp,
  LocalModelEngineInfo,
  LocalModelInfo,
  LocalServerStatus,
  LogitechDevice,
  MacroStep,
  OverlayOptions,
  Overview,
  PluginCatalog,
  ProcessInfo,
  ProxyStatus,
  ScheduledTask,
  StartupItem,
  VendorTelemetry,
  WindowsLogReport,
} from './types'
import './App.css'

type Page =
  | 'overview'
  | 'devices'
  | 'performance'
  | 'gpu-unlock'
  | 'network'
  | 'clipboard'
  | 'files'
  | 'audio'
  | 'shortcuts'
  | 'launcher'
  | 'device-manager'
  | 'uninstaller'
  | 'disk'
  | 'drivers'
  | 'models'
  | 'logs'
  | 'tasks'
  | 'macros'
  | 'settings'
  | 'plugins'

type NavigationGroupId =
  | 'overview'
  | 'hardware'
  | 'toolbox'
  | 'network'
  | 'automation'
  | 'system'

const overlayMetricOptions = [
  ['cpuUsage', 'CPU 使用率'],
  ['cpuTemp', 'CPU 温度'],
  ['cpuFan', 'CPU 风扇'],
  ['gpuUsage', 'GPU 使用率'],
  ['gpuTemp', 'GPU 温度'],
  ['gpuFan', 'GPU 风扇'],
  ['gpuMemory', '显存占用'],
  ['memory', '内存占用'],
  ['networkDown', '下载速度'],
  ['networkUp', '上传速度'],
  ['fps', '帧率 / 刷新率'],
  ['battery', '系统电池'],
  ['bluetooth', '蓝牙设备电量'],
  ['disk', '磁盘占用'],
  ['uptime', '运行时间'],
] as const

const pageIcons: Record<Page, IconName> = {
  overview: 'dashboard',
  launcher: 'rocket',
  devices: 'monitor',
  performance: 'gauge',
  'gpu-unlock': 'bolt',
  network: 'network',
  clipboard: 'clipboard',
  files: 'search',
  audio: 'volume',
  shortcuts: 'keyboard',
  'device-manager': 'plug',
  uninstaller: 'trash',
  disk: 'pie',
  drivers: 'driver',
  models: 'brain',
  logs: 'log',
  tasks: 'clock',
  macros: 'mouse',
  settings: 'settings',
  plugins: 'grid',
}

const navigation: Array<{
  id: NavigationGroupId
  label: string
  icon: IconName
  pages: Page[]
}> = [
  { id: 'overview', label: '总览', icon: 'dashboard', pages: ['overview'] },
  {
    id: 'hardware',
    label: '硬件控制',
    icon: 'cpu',
    pages: ['performance', 'gpu-unlock', 'devices', 'device-manager', 'drivers'],
  },
  {
    id: 'toolbox',
    label: '常用工具',
    icon: 'rocket',
    pages: ['launcher', 'clipboard', 'files', 'audio', 'disk', 'uninstaller'],
  },
  { id: 'network', label: '网络代理', icon: 'network', pages: ['network'] },
  {
    id: 'automation',
    label: '自动化',
    icon: 'clock',
    pages: ['shortcuts', 'macros', 'tasks'],
  },
  {
    id: 'system',
    label: '系统与模型',
    icon: 'settings',
    pages: ['plugins', 'models', 'logs', 'settings'],
  },
]

const corePages = new Set<Page>([
  'overview',
  'launcher',
  'network',
  'clipboard',
  'files',
  'audio',
  'shortcuts',
  'plugins',
  'settings',
])

const pagePlugin: Partial<Record<Page, string>> = {
  performance: 'hardware',
  devices: 'hardware',
  'device-manager': 'hardware',
  drivers: 'hardware',
  'gpu-unlock': 'game-enhancements',
  uninstaller: 'system-tools',
  disk: 'system-tools',
  logs: 'system-tools',
  macros: 'automation',
  tasks: 'automation',
  models: 'ai-workbench',
}

const isPageAvailable = (page: Page, enabledPlugins: string[]) =>
  corePages.has(page) ||
  (pagePlugin[page] != null && enabledPlugins.includes(pagePlugin[page]!))

const pageTitles: Record<Page, { title: string; description: string }> = {
  overview: { title: '本地控制台', description: '一台电脑需要的常用控制，集中在一个低权限窗口里。' },
  launcher: { title: '快捷启动', description: '复用文件索引，游戏与应用优先，常用程序自动前移。' },
  devices: { title: '显示与外设', description: '显示器亮度、鼠标系统灵敏度、Logitech HID++ 电量与 DPI。' },
  performance: { title: '性能与散热', description: '识别当前主板与厂商面板，集中管理风扇、功耗和 NVIDIA GPU 调校。' },
  'gpu-unlock': { title: 'GPU 解锁与 DLSS', description: '预启动 GPU profile 兼容检查，以及带备份回滚的 DLSS 神经渲染部署。' },
  network: { title: '代理网络', description: '本地流量转发默认开启；系统代理默认关闭，需要时再手动接管。' },
  clipboard: { title: '剪贴板与截图', description: '本地 DPAPI 加密历史、固定片段、区域截图与贴图。' },
  files: { title: '文件搜索', description: '自己的 SQLite 全文索引，不依赖 Everything，也不上传文件名。' },
  audio: { title: '应用音量', description: '按应用调节音量与静音，对应 EarTrumpet 的常用本地功能。' },
  shortcuts: { title: '快捷键中心', description: '全局热键独立管理，截图、贴图、面板、宏和自定义命令都能复用。' },
  'device-manager': { title: '设备管理器', description: '集中查看显示器、鼠标、键盘、HID、音频和 USB 外设。' },
  uninstaller: { title: '卸载与残留清理', description: '读取本机卸载登记，卸载前可扫描目录和损坏注册表项。' },
  disk: { title: '空间分析', description: '按需扫描目录并生成可读的分区占用图，不常驻后台。' },
  drivers: { title: '驱动检测', description: '查看已安装驱动，并按需查询 Windows Update 提供的驱动更新。' },
  models: { title: '模型中心', description: '本地 GGUF 模型、TPS 估算/实测参数和常用云端模型入口。' },
  logs: { title: '系统日志', description: '聚合最近的系统/应用错误、崩溃和蓝屏转储信息。' },
  tasks: { title: '启动与计划', description: '管理开机启动项和 Windows 计划任务，不再跳转外部管理器。' },
  macros: { title: '侧键与宏', description: 'Logitech 侧键绑定全局宏，编辑器和快捷键页面共用同一套前端组件。' },
  settings: { title: '设置与隐私', description: '所有设置保存在本机；没有更新检查、遥测或账号组件。' },
  plugins: { title: '能力检测与插件', description: '安装后先检测本机硬件和系统能力，再按需启用功能插件。' },
}

function MainApplication() {
  const [page, setPage] = useState<Page>('overview')
  const [bootstrap, setBootstrap] = useState<Bootstrap | null>(null)
  const [settings, setSettings] = useState<AppSettings | null>(null)
  const [overview, setOverview] = useState<Overview | null>(null)
  const [displays, setDisplays] = useState<DisplayBrightness[]>([])
  const [devices, setDevices] = useState<LogitechDevice[]>([])
  const [proxy, setProxy] = useState<ProxyStatus | null>(null)
  const [clipboard, setClipboard] = useState<ClipboardEntry[]>([])
  const [fileStatus, setFileStatus] = useState<FileIndexStatus | null>(null)
  const [audio, setAudio] = useState<AudioSession[]>([])
  const [deviceManager, setDeviceManager] = useState<DeviceInfo[]>([])
  const [startupItems, setStartupItems] = useState<StartupItem[]>([])
  const [scheduledTasks, setScheduledTasks] = useState<ScheduledTask[]>([])
  const [hotkeyStatus, setHotkeyStatus] = useState<HotkeyRegistration[]>([])
  const [hardware, setHardware] = useState<HardwareProfile | null>(null)
  const [gpuControl, setGpuSnapshot] = useState<GpuControlSnapshot | null>(null)
  const [gpuUnlock, setGpuUnlock] = useState<GpuUnlockStatus | null>(null)
  const [dlssUnlocked, setDlssUnlocked] = useState<DlssUnlockedStatus | null>(null)
  const [pluginCatalog, setPluginCatalog] = useState<PluginCatalog | null>(null)
  const [toast, setToast] = useState('')
  const [fatal, setFatal] = useState('')
  const toastTimer = useRef<number | null>(null)

  const notify = useCallback((message: string) => {
    setToast(message)
    if (toastTimer.current) {
      window.clearTimeout(toastTimer.current)
    }
    toastTimer.current = window.setTimeout(() => setToast(''), 2600)
  }, [])

  const refreshOverview = useCallback(async () => {
    try {
      const next = await invoke<Overview>('getOverview')
      setOverview(next)
      setFileStatus(next.index)
    } catch {
      // Polling failures are transient while a heavy index scan starts.
    }
  }, [])

  const refreshDisplays = useCallback(async () => {
    try {
      setDisplays(await invoke<DisplayBrightness[]>('getDisplays'))
    } catch {
      // DDC/CI monitors can briefly disappear while the display sleeps.
    }
  }, [])

  const refreshDevices = useCallback(async () => {
    try {
      setDevices(await invoke<LogitechDevice[]>('getLogitechDevices'))
    } catch {
      // A receiver can reconnect while polling.
    }
  }, [])

  const refreshProxy = useCallback(async () => {
    try {
      setProxy(await invoke<ProxyStatus>('getProxyStatus'))
    } catch (error) {
      setProxy((current) => (current ? { ...current, error: messageOf(error) } : current))
    }
  }, [])

  const refreshSettings = useCallback(async () => {
    try {
      setSettings(await invoke<AppSettings>('getSettings'))
    } catch {
      // A settings write can overlap a short-lived file lock.
    }
  }, [])

  const refreshDeviceManager = useCallback(async () => {
    try {
      setDeviceManager(await invoke<DeviceInfo[]>('getDevices'))
    } catch {
      // Plug and play can disappear while the list is being read.
    }
  }, [])

  const refreshTasks = useCallback(async () => {
    try {
      const [startup, tasks] = await Promise.all([
        invoke<StartupItem[]>('getStartupItems'),
        invoke<ScheduledTask[]>('getScheduledTasks'),
      ])
      setStartupItems(startup)
      setScheduledTasks(tasks)
    } catch {
      // Task Scheduler may deny access to individual protected folders.
    }
  }, [])

  const refreshHotkeys = useCallback(async () => {
    try {
      setHotkeyStatus(await invoke<HotkeyRegistration[]>('getHotkeyStatus'))
    } catch {
      // Hotkey registration status is best-effort.
    }
  }, [])

  const refreshClipboard = useCallback(async () => {
    try {
      setClipboard(await invoke<ClipboardEntry[]>('getClipboardHistory'))
    } catch {
      // Clipboard can be locked by a focused application for a few milliseconds.
    }
  }, [])

  const refreshFiles = useCallback(async () => {
    try {
      setFileStatus(await invoke<FileIndexStatus>('getFileIndexStatus'))
    } catch {
      // Ignore status polling errors.
    }
  }, [])

  const refreshAudio = useCallback(async () => {
    try {
      setAudio(await invoke<AudioSession[]>('getAudioSessions'))
    } catch {
      // Audio session enumeration can race with process exits.
    }
  }, [])

  const refreshHardwareControl = useCallback(async () => {
    try {
      const [profile, gpu] = await Promise.all([
        invoke<HardwareProfile>('getHardwareProfile'),
        invoke<GpuControlSnapshot>('getGpuControl'),
      ])
      setHardware(profile)
      setGpuSnapshot(gpu)
    } catch {
      // Vendor WMI and NVML can be unavailable while drivers are restarting.
    }
  }, [])

  const refreshUnlockTools = useCallback(async () => {
    try {
      const [unlock, dlss] = await Promise.all([
        invoke<GpuUnlockStatus>('getGpuUnlockStatus'),
        invoke<DlssUnlockedStatus>('getDlssUnlockedStatus'),
      ])
      setGpuUnlock(unlock)
      setDlssUnlocked(dlss)
    } catch {
      // The profile registry and package cache are local and optional.
    }
  }, [])

  const refreshPluginCatalog = useCallback(async (refresh = false) => {
    try {
      const catalog = await invoke<PluginCatalog>('getPluginCatalog', { refresh })
      setPluginCatalog(catalog)
      return catalog
    } catch {
      return null
    }
  }, [])

  useEffect(() => {
    let cancelled = false
    invoke<Bootstrap>('bootstrap')
      .then((result) => {
        if (cancelled) return
        setBootstrap(result)
        setSettings(result.settings)
        setFileStatus(result.capabilities.searchIndex)
        if (!result.settings.pluginOnboardingCompleted) {
          setPage('plugins')
        }
      })
      .catch((error) => setFatal(messageOf(error)))

    void refreshOverview()
    void refreshDisplays()
    void refreshDevices()
    void refreshProxy()
    void refreshClipboard()

    const timers = [
      window.setInterval(() => void refreshOverview(), 2400),
      window.setInterval(() => void refreshProxy(), 4000),
      window.setInterval(() => void refreshDisplays(), 9000),
      window.setInterval(() => void refreshDevices(), 30_000),
      window.setInterval(() => void refreshFiles(), 1800),
    ]

    const unsubscribe = onNativeEvent((event) => {
      if (event.action === 'show-clipboard') {
        setPage('clipboard')
        void refreshClipboard()
      } else if (event.action === 'focus-search') {
        setPage('files')
      } else if (event.action === 'clipboard-updated') {
        void refreshClipboard()
      } else if (event.action === 'screenshot-captured') {
        void refreshClipboard()
        notify('截图已复制到剪贴板')
      } else if (event.action === 'pin-image-empty') {
        notify('剪贴板中没有图片')
      } else if (event.action === 'proxy-updated') {
        void refreshProxy()
      } else if (event.action === 'show-launcher') {
        setPage('launcher')
      } else if (event.action === 'settings-updated') {
        void refreshProxy()
        void refreshTasks()
      } else if (event.action === 'performance-overlay-updated') {
        void refreshSettings()
      } else if (event.action === 'plugin-required') {
        setPage('plugins')
      }
    })

    return () => {
      cancelled = true
      timers.forEach((timer) => window.clearInterval(timer))
      unsubscribe()
    }
  }, [
    notify,
    refreshAudio,
    refreshProxy,
    refreshSettings,
    refreshClipboard,
    refreshDeviceManager,
    refreshDevices,
    refreshDisplays,
    refreshFiles,
    refreshOverview,
    refreshPluginCatalog,
    refreshTasks,
  ])

  useEffect(() => {
    if (page === 'device-manager') {
      void refreshDeviceManager()
    } else if (page === 'performance') {
      void refreshHardwareControl()
      const timer = window.setInterval(() => void refreshHardwareControl(), 4000)
      return () => window.clearInterval(timer)
    } else if (page === 'gpu-unlock') {
      void refreshUnlockTools()
      const timer = window.setInterval(() => void refreshUnlockTools(), 1600)
      return () => window.clearInterval(timer)
    } else if (page === 'plugins') {
      void refreshPluginCatalog()
    } else if (page === 'tasks') {
      void refreshTasks()
    } else if (page === 'shortcuts') {
      void refreshHotkeys()
    } else if (page === 'audio') {
      void refreshAudio()
      const timer = window.setInterval(() => void refreshAudio(), 4500)
      return () => window.clearInterval(timer)
    }
  }, [
    page,
    refreshAudio,
    refreshDeviceManager,
    refreshHardwareControl,
    refreshHotkeys,
    refreshPluginCatalog,
    refreshTasks,
    refreshUnlockTools,
  ])

  const updateBrightness = (id: string, value: number) => {
    setDisplays((items) => items.map((item) => (item.id === id ? { ...item, current: value } : item)))
  }

  const commitBrightness = async (id: string, value: number) => {
    try {
      const result = await invoke<{ ok: boolean }>('setBrightness', { id, value })
      if (!result.ok) notify('这个显示器不接受当前亮度命令')
    } catch (error) {
      notify(messageOf(error))
    }
  }

  const setPerformanceMode = async (mode: string) => {
    try {
      const result = await invoke<{ ok: boolean }>('setPerformanceMode', { mode })
      notify(result.ok ? `已切换到${modeLabel(mode)}` : '厂商控制接口不可用')
      void refreshOverview()
    } catch (error) {
      notify(messageOf(error))
    }
  }

  const setPointerSpeed = async (value: number) => {
    try {
      await invoke('setPointerSpeed', { value })
      notify(`系统鼠标灵敏度 ${value}/20`)
    } catch (error) {
      notify(messageOf(error))
    }
  }

  const setLogitechDpi = async (id: string, dpi: number) => {
    try {
      const result = await invoke<{ ok: boolean }>('setLogitechDpi', { id, dpi })
      notify(result.ok ? `DPI 已设置为 ${dpi}` : 'DPI 设置失败，请确认设备未被独占')
      void refreshDevices()
    } catch (error) {
      notify(messageOf(error))
    }
  }

  const setProxyMode = async (mode: string) => {
    try {
      setProxy(await invoke<ProxyStatus>('setProxyMode', { mode }))
      notify(`代理模式：${modeLabel(mode)}`)
    } catch (error) {
      notify(messageOf(error))
    }
  }

  const applyGpuControl = async (kind: string, value: number) => {
    try {
      const result = await invoke<GpuControlResult>('setGpuControl', { kind, value })
      if (result.snapshot) {
        setGpuSnapshot(result.snapshot)
      }
      notify(result.message)
      return result.ok
    } catch (error) {
      notify(messageOf(error))
      return false
    }
  }

  const setSystemProxy = async (enabled: boolean) => {
    try {
      setProxy(await invoke<ProxyStatus>('setProxySystemProxy', { enabled }))
      notify(enabled ? '系统代理已开启' : '系统代理已关闭')
    } catch (error) {
      notify(messageOf(error))
    }
  }

  const captureScreenshot = async () => {
    try {
      await invoke('captureRegion')
      await refreshClipboard()
    } catch (error) {
      notify(messageOf(error))
    }
  }

  const captureScreenshotAndPin = async () => {
    try {
      await invoke('captureRegionAndPin')
      await refreshClipboard()
    } catch (error) {
      notify(messageOf(error))
    }
  }

  const saveSettings = async (next: AppSettings, message = '设置已保存') => {
    try {
      const saved = await invoke<AppSettings>('saveSettings', { settings: next })
      setSettings(saved)
      notify(message)
    } catch (error) {
      notify(messageOf(error))
    }
  }

  const updatePerformanceOverlay = async (patch: {
    enabled?: boolean
    opacity?: number
    fpsSource?: string
    metrics?: string[]
    width?: number
    height?: number
  }) => {
    try {
      const saved = await invoke<AppSettings>('updatePerformanceOverlay', patch)
      setSettings(saved)
      return saved
    } catch (error) {
      notify(messageOf(error))
      return null
    }
  }

  const title = pageTitles[page]
  const enabledPlugins = settings?.enabledPlugins ?? []
  const visibleNavigation = navigation
    .map((group) => ({
      ...group,
      pages: group.pages.filter((item) => isPageAvailable(item, enabledPlugins)),
    }))
    .filter((group) => group.pages.length > 0)
  const activeGroup =
    visibleNavigation.find((group) => group.pages.includes(page)) ??
    visibleNavigation[0] ??
    navigation[0]
  const version = bootstrap?.app.version ?? '0.1.0'

  useEffect(() => {
    if (settings && !isPageAvailable(page, settings.enabledPlugins)) {
      setPage('plugins')
    }
  }, [page, settings])

  if (fatal) {
    return (
      <div className="fatal-screen">
        <div className="brand-mark">LD</div>
        <h1>LocalDeck 无法连接本地后端</h1>
        <p>{fatal}</p>
        <button className="button primary" onClick={() => window.location.reload()}>
          重试
        </button>
      </div>
    )
  }

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand">
          <div className="brand-mark">LD</div>
          <div>
            <strong>LocalDeck</strong>
            <span>LOCAL-FIRST CONTROL</span>
          </div>
        </div>
        <nav className="nav-list" aria-label="主导航">
          {visibleNavigation.map((item) => (
            <button
              key={item.id}
              className={`nav-item ${activeGroup.id === item.id ? 'active' : ''}`}
              onClick={() =>
                setPage(item.pages.includes(page) ? page : item.pages[0])}
            >
              <Icon name={item.icon} />
              <span>
                {item.label}
                <em>{item.pages.length}</em>
              </span>
            </button>
          ))}
        </nav>
        <div className="sidebar-footer">
          <div className="privacy-dot" />
          <div>
            <strong>仅本机</strong>
            <span>无遥测 · v{version}</span>
          </div>
        </div>
      </aside>

      <main className="workspace">
        <header className="page-header">
          <div>
            <p className="eyebrow">{page === 'overview' ? bootstrap?.app.machine ?? 'WINDOWS' : 'LOCALDECK MODULE'}</p>
            <h1>{title.title}</h1>
            <p>{title.description}</p>
          </div>
          <div className="header-actions">
            <button
              className="button icon-button"
              title="性能悬浮窗"
              onClick={async () => {
                const result = await invoke<{ enabled: boolean }>('togglePerformanceOverlay')
                notify(result.enabled ? '性能悬浮窗已显示' : '性能悬浮窗已隐藏')
              }}
            >
              <Icon name="gauge" />
            </button>
            <button className="button icon-button" title="截图" onClick={() => void captureScreenshot()}>
              <Icon name="camera" />
            </button>
            <div className={`live-chip ${proxy?.running ? 'on' : ''}`}>
              <span />
              {proxy?.running ? '流量转发开启' : '流量转发关闭'}
            </div>
          </div>
        </header>

        <nav className="toolbox-dock" aria-label={`${activeGroup.label}工具箱`}>
          <div className="toolbox-caption">
            <Icon name={activeGroup.icon} />
            <div>
              <span>{activeGroup.label}</span>
              <b>TOOL WINDOW</b>
            </div>
          </div>
          <div className="toolbox-tabs">
            {activeGroup.pages.map((item) => (
              <button
                key={item}
                className={`toolbox-tab ${page === item ? 'active' : ''}`}
                onClick={() => setPage(item)}
              >
                <Icon name={pageIcons[item]} />
                <span>{pageTitles[item].title}</span>
              </button>
            ))}
          </div>
        </nav>

        <div className="page-content tool-window-content">
          {page === 'overview' && (
            <OverviewPage
              overview={overview}
              displays={displays}
              proxy={proxy}
              devices={devices}
              onBrightness={updateBrightness}
              onBrightnessCommit={(id, value) => void commitBrightness(id, value)}
              onNavigate={setPage}
            />
          )}
          {page === 'launcher' && <LauncherPage />}
          {page === 'devices' && (
            <DevicesPage
              displays={displays}
              devices={devices}
              pointerSpeed={overview?.pointerSpeed ?? 10}
              onBrightness={updateBrightness}
              onBrightnessCommit={(id, value) => void commitBrightness(id, value)}
              onPointerSpeed={(value) => void setPointerSpeed(value)}
              onDpi={(id, dpi) => void setLogitechDpi(id, dpi)}
              onRefresh={() => void refreshDevices()}
            />
          )}
          {page === 'performance' && (
            <PerformancePage
              vendor={overview?.vendor ?? null}
              hardware={hardware}
              gpu={gpuControl}
              processes={overview?.system.topProcesses ?? []}
              onMode={(mode) => void setPerformanceMode(mode)}
              onGpu={(kind, value) => void applyGpuControl(kind, value)}
            />
          )}
          {page === 'gpu-unlock' && (
            <GpuUnlockPage
              unlock={gpuUnlock}
              dlss={dlssUnlocked}
              onRefresh={() => void refreshUnlockTools()}
              onExportReport={async () => {
                const result = await invoke<{ path: string }>('exportGpuUnlockReport')
                notify(`兼容报告已导出：${result.path}`)
              }}
              onOpenUnlockUpstream={() => void invoke('openGpuUnlockUpstream')}
              onDownload={async () => {
                setDlssUnlocked(await invoke<DlssUnlockedStatus>('startDlssDownload'))
                notify('DLSS Unlocked 安装包开始下载')
              }}
              onCancelDownload={async () => {
                setDlssUnlocked(await invoke<DlssUnlockedStatus>('cancelDlssDownload'))
              }}
              onChooseGame={async () => {
                const result = await invoke<{ path: string | null }>('chooseDlssGameFolder')
                return result.path
              }}
              onInstall={async (path, proxyName) => {
                setDlssUnlocked(await invoke<DlssUnlockedStatus>('startDlssInstall', {
                  path,
                  proxyName,
                }))
                notify('DLSS Unlocked 正在部署到游戏目录')
              }}
              onUninstall={async (id) => {
                setDlssUnlocked(await invoke<DlssUnlockedStatus>('startDlssUninstall', { id }))
                notify('正在恢复游戏原始文件')
              }}
              onOpenInstallation={(id) => void invoke('openDlssInstallation', { id })}
              onOpenDlssUpstream={() => void invoke('openDlssUpstream')}
            />
          )}
          {page === 'network' && (
            <NetworkPage
              proxy={proxy}
              onMode={(mode) => void setProxyMode(mode)}
              onProxy={(enabled) => void setSystemProxy(enabled)}
              onRefresh={() => void refreshProxy()}
              onStart={async () => {
                try {
                  setProxy(await invoke<ProxyStatus>('startProxy'))
                  notify('流量转发接口已启动')
                } catch (error) {
                  notify(messageOf(error))
                }
              }}
              onStop={async () => {
                setProxy(await invoke<ProxyStatus>('stopProxy'))
                  notify('流量转发接口已停止')
              }}
              onSelectNode={async (name) => {
                setProxy(await invoke<ProxyStatus>('selectProxyNode', { name }))
                notify(`已选择 ${name}`)
              }}
              onImport={async () => {
                setProxy(await invoke<ProxyStatus>('importProxyProfile'))
                notify('代理 YAML 已导入')
              }}
            />
          )}
          {page === 'clipboard' && (
            <ClipboardPage
              entries={clipboard}
              onRefresh={() => void refreshClipboard()}
              onCapture={() => void captureScreenshotAndPin()}
              onCopy={async (id) => {
                await invoke('copyClipboardEntry', { id })
                notify('已复制回剪贴板')
              }}
              onPin={async (id) => setClipboard(await invoke<ClipboardEntry[]>('toggleClipboardPin', { id }))}
              onDelete={async (id) => setClipboard(await invoke<ClipboardEntry[]>('deleteClipboardEntry', { id }))}
              onClear={async (includePinned) => {
                setClipboard(await invoke<ClipboardEntry[]>('clearClipboardHistory', { includePinned }))
                notify('历史已清理')
              }}
              onAddSnippet={async (text) => {
                setClipboard(await invoke<ClipboardEntry[]>('addClipboardSnippet', { text }))
                notify('片段已固定')
              }}
            />
          )}
          {page === 'files' && (
            <FilesPage
              status={fileStatus}
              roots={settings?.searchRoots ?? []}
              onStatus={setFileStatus}
              onRootsChange={async (roots) => {
                if (settings) {
                  await saveSettings({ ...settings, searchRoots: roots }, '搜索目录已更新')
                }
              }}
              onChooseRoot={async () => {
                const result = await invoke<{ path: string | null }>('chooseSearchRoot')
                if (result.path && settings) {
                  await saveSettings(
                    { ...settings, searchRoots: [...settings.searchRoots, result.path] },
                    '已添加搜索目录',
                  )
                }
              }}
              onReindex={async () => {
                await invoke('startFileIndex')
                notify('索引已重新开始')
                void refreshFiles()
              }}
              onStop={async () => {
                await invoke('stopFileIndex')
                notify('索引已停止')
                void refreshFiles()
              }}
            />
          )}
          {page === 'audio' && <AudioPage sessions={audio} onRefresh={() => void refreshAudio()} />}
          {page === 'shortcuts' && settings && (
            <ShortcutCenter
              settings={settings}
              registrations={hotkeyStatus}
              onSave={async (next) => {
                await saveSettings(next, '快捷键已保存并重新注册')
                await refreshHotkeys()
              }}
            />
          )}
          {page === 'device-manager' && (
            <DeviceManagerPage
              devices={deviceManager}
              onRefresh={() => void refreshDeviceManager()}
              onToggle={async (device, enabled) => {
                const result = await invoke<{ ok: boolean }>('setDeviceEnabled', {
                  instanceId: device.instanceId,
                  enabled,
                })
                if (!result.ok) notify('需要管理员权限，或该设备被保护')
                void refreshDeviceManager()
              }}
              onProperties={async (device) => {
                await invoke('openDeviceProperties', { instanceId: device.instanceId })
              }}
              onOpenWindows={async () => invoke('openWindowsDeviceManager')}
              onRescan={async () => {
                const result = await invoke<{ ok: boolean }>('rescanDevices')
                notify(result.ok ? '设备扫描已刷新' : '重新扫描需要管理员权限')
                void refreshDeviceManager()
              }}
            />
          )}
          {page === 'uninstaller' && <UninstallerPage />}
          {page === 'disk' && <DiskAnalyzerPage />}
          {page === 'drivers' && <DriverPage />}
          {page === 'models' && settings && (
            <ModelCenterPage
              settings={settings}
              onSave={(next, message) => saveSettings(next, message)}
            />
          )}
          {page === 'logs' && <WindowsLogPage />}
          {page === 'tasks' && (
            <TaskCenter
              startup={startupItems}
              tasks={scheduledTasks}
              onRefresh={() => void refreshTasks()}
              onCleanup={async () => {
                const result = await invoke<{ removed: string[]; failed: string[] }>('cleanupOrphanedStartup')
                notify(result.removed.length
                  ? `已清理 ${result.removed.length} 个失效启动项`
                  : '没有发现可安全清理的失效启动项')
                void refreshTasks()
              }}
              onStartupToggle={async (item, enabled) => {
                const result = await invoke<{ ok: boolean }>('setStartupEnabled', { item, enabled })
                notify(result.ok ? '启动项已更新' : '需要管理员权限才能修改该启动项')
                void refreshTasks()
              }}
              onTaskToggle={async (task, enabled) => {
                const result = await invoke<{ ok: boolean }>('setScheduledTaskEnabled', { item: task, enabled })
                notify(result.ok ? '计划任务状态已更新' : '无法修改该计划任务')
                void refreshTasks()
              }}
              onTaskRun={async (task) => {
                const result = await invoke<{ ok: boolean }>('runScheduledTask', { item: task })
                notify(result.ok ? '计划任务已运行' : '计划任务无法运行')
                void refreshTasks()
              }}
              onTaskStop={async (task) => {
                const result = await invoke<{ ok: boolean }>('stopScheduledTask', { item: task })
                notify(result.ok ? '计划任务已停止' : '计划任务无法停止')
                void refreshTasks()
              }}
            />
          )}
          {page === 'macros' && settings && (
            <MacroPage
              settings={settings}
              onSave={async (next) => saveSettings(next, '侧键与宏已保存')}
            />
          )}
          {page === 'plugins' && (
            <PluginCenterPage
              catalog={pluginCatalog}
              onRefresh={() => void refreshPluginCatalog(true)}
              onToggle={async (id, enabled) => {
                const catalog = await invoke<PluginCatalog>('setPluginEnabled', { id, enabled })
                setPluginCatalog(catalog)
                await refreshSettings()
                notify(enabled ? '插件包已启用' : '插件包已停用')
              }}
              onComplete={async () => {
                const catalog = await invoke<PluginCatalog>('completePluginOnboarding')
                setPluginCatalog(catalog)
                await refreshSettings()
                setPage('overview')
                notify('能力检测已完成')
              }}
            />
          )}
          {page === 'settings' && settings && (
            <SettingsPage
              settings={settings}
              app={bootstrap?.app ?? null}
              onSave={saveSettings}
              onOverlayChange={updatePerformanceOverlay}
              onOpenData={() => void invoke('openDataFolder')}
            />
          )}
        </div>
      </main>

      {toast && <div className="toast">{toast}</div>}
    </div>
  )
}

function OverviewPage({
  overview,
  displays,
  proxy,
  devices,
  onBrightness,
  onBrightnessCommit,
  onNavigate,
}: {
  overview: Overview | null
  displays: DisplayBrightness[]
  proxy: ProxyStatus | null
  devices: LogitechDevice[]
  onBrightness: (id: string, value: number) => void
  onBrightnessCommit: (id: string, value: number) => void
  onNavigate: (page: Page) => void
}) {
  const system = overview?.system
  const vendor = overview?.vendor
  const mouse = devices.find((device) => device.available)
  return (
    <>
      <section className="metric-grid">
        <Metric
          icon="cpu"
          label="CPU 负载"
          value={system ? `${system.cpuPercent.toFixed(0)}%` : '—'}
          detail={vendor?.cpuTemperature ? `${vendor.cpuTemperature}°C · ${vendor.cpuFanRpm ?? '—'} RPM` : '本地采样'}
          progress={system?.cpuPercent ?? 0}
          tone="mint"
        />
        <Metric
          icon="memory"
          label="内存"
          value={system ? formatBytes(system.memoryUsedBytes) : '—'}
          detail={system ? `共 ${formatBytes(system.memoryTotalBytes)}` : '等待采样'}
          progress={system ? (system.memoryUsedBytes / system.memoryTotalBytes) * 100 : 0}
          tone="blue"
        />
        <Metric
          icon="fan"
          label="GPU 风扇"
          value={vendor?.gpuFanRpm ? `${vendor.gpuFanRpm} RPM` : '—'}
          detail={vendor?.gpuTemperature ? `GPU ${vendor.gpuTemperature}°C` : vendor?.error ?? '厂商接口未加载'}
          progress={Math.min(100, (vendor?.gpuFanRpm ?? 0) / 45)}
          tone="orange"
        />
        <Metric
          icon="battery"
          label="设备电量"
          value={
            mouse?.batteryPercent != null
              ? `${mouse.batteryPercent}%`
              : system?.batteryPercent != null
                ? `${system.batteryPercent}%`
                : '—'
          }
          detail={mouse ? mouse.name : system?.batteryCharging ? '电源已连接' : '本机电池'}
          progress={mouse?.batteryPercent ?? system?.batteryPercent ?? 0}
          tone="violet"
        />
      </section>

      <div className="dashboard-columns">
        <section className="panel">
          <PanelHeading title="显示器亮度" action={<button className="text-button" onClick={() => onNavigate('devices')}>全部设备</button>} />
          <div className="brightness-list">
            {displays.length === 0 && <EmptyState text="暂未检测到可调亮度显示器" />}
            {displays.map((display) => (
              <div className="brightness-row" key={display.id}>
                <div className="device-icon"><Icon name="sun" /></div>
                <div className="range-field">
                  <div className="range-label">
                    <div>
                      <strong>{display.name}</strong>
                      <span>{display.transport}</span>
                    </div>
                    <b>{display.current}%</b>
                  </div>
                  <input
                    className="range"
                    type="range"
                    min={0}
                    max={100}
                    value={display.current}
                    onChange={(event) => onBrightness(display.id, Number(event.target.value))}
                    onPointerUp={(event) => onBrightnessCommit(display.id, Number(event.currentTarget.value))}
                    onKeyUp={(event) => onBrightnessCommit(display.id, Number(event.currentTarget.value))}
                  />
                </div>
              </div>
            ))}
          </div>
        </section>

        <section className="panel status-panel">
          <PanelHeading title="整合状态" action={<span className="small-note">LOCAL ONLY</span>} />
          <StatusLine
            icon="network"
            title="流量转发"
            value={proxy?.running ? `监听 ${proxy.listenAddress}:${proxy.port}` : '未启动'}
            detail={proxy?.systemProxyEnabled ? '系统代理按需开启' : '系统代理关闭 · 仅本地端口'}
            ok={Boolean(proxy?.running)}
          />
          <StatusLine
            icon="gauge"
            title="厂商控制"
            value={vendor?.source ?? '不可用'}
            detail={vendor?.cpuFanRpm ? `CPU 风扇 ${vendor.cpuFanRpm} RPM` : vendor?.error ?? '等待 Pegatron 服务'}
            ok={Boolean(vendor?.source)}
          />
          <StatusLine
            icon="search"
            title="文件索引"
            value={overview?.index.indexing ? '正在建立索引' : `${overview?.index.fileCount.toLocaleString() ?? 0} 个文件`}
            detail={overview?.index.lastIndexed ? `最近更新 ${formatTime(overview.index.lastIndexed)}` : '尚未完成首次索引'}
            ok={Boolean(overview?.index.lastIndexed)}
          />
          <div className="network-rate">
            <span><Icon name="download" /> {formatRate(system?.downloadBytesPerSecond ?? 0)}</span>
            <span><Icon name="upload" /> {formatRate(system?.uploadBytesPerSecond ?? 0)}</span>
            <span>运行 {system?.uptime ?? '—'}</span>
          </div>
        </section>
      </div>

      <div className="dashboard-columns lower">
        <section className="panel">
          <PanelHeading title="存储空间" action={<span className="small-note">固定磁盘</span>} />
          <div className="disk-list">
            {(system?.disks ?? []).map((disk) => {
              const used = disk.totalBytes - disk.freeBytes
              return (
                <div className="disk-row" key={disk.name}>
                  <div className="disk-letter">{disk.name.replace('\\', '')}</div>
                  <div>
                    <div className="range-label">
                      <strong>{disk.label || '本地磁盘'}</strong>
                      <span>{formatBytes(disk.freeBytes)} 可用</span>
                    </div>
                    <div className="progress-track">
                      <div style={{ width: `${(used / disk.totalBytes) * 100}%` }} />
                    </div>
                  </div>
                </div>
              )
            })}
          </div>
        </section>
        <section className="panel">
          <PanelHeading title="高占用进程" action={<button className="text-button" onClick={() => onNavigate('performance')}>更多</button>} />
          <ProcessList processes={system?.topProcesses ?? []} />
        </section>
      </div>
    </>
  )
}

function DevicesPage({
  displays,
  devices,
  pointerSpeed,
  onBrightness,
  onBrightnessCommit,
  onPointerSpeed,
  onDpi,
  onRefresh,
}: {
  displays: DisplayBrightness[]
  devices: LogitechDevice[]
  pointerSpeed: number
  onBrightness: (id: string, value: number) => void
  onBrightnessCommit: (id: string, value: number) => void
  onPointerSpeed: (value: number) => void
  onDpi: (id: string, dpi: number) => void
  onRefresh: () => void
}) {
  const [localPointer, setLocalPointer] = useState(pointerSpeed)
  useEffect(() => setLocalPointer(pointerSpeed), [pointerSpeed])
  return (
    <div className="two-column">
      <section className="panel">
        <PanelHeading title="显示器" action={<span className="badge">WMI · DDC/CI · Gamma</span>} />
        <div className="stack">
          {displays.map((display) => (
            <div className="control-card" key={display.id}>
              <div className="card-heading">
                <div className="device-icon"><Icon name="monitor" /></div>
                <div>
                  <strong>{display.name}</strong>
                  <span>{display.transport} · {display.maximum - display.minimum + 1} 级</span>
                  {display.error && <em className="display-fallback-note">{display.error}</em>}
                </div>
                <b>{display.current}%</b>
              </div>
              <input
                className="range"
                type="range"
                min={display.minimum}
                max={display.maximum}
                value={display.current}
                disabled={!display.writable}
                onChange={(event) => onBrightness(display.id, Number(event.target.value))}
                onPointerUp={(event) => onBrightnessCommit(display.id, Number(event.currentTarget.value))}
              />
            </div>
          ))}
          {displays.length === 0 && <EmptyState text="没有识别到活动显示器" />}
        </div>
      </section>

      <div className="stack">
        <section className="panel">
          <PanelHeading title="指针灵敏度" action={<span className="small-note">Windows 全局</span>} />
          <div className="large-number">{localPointer}<span>/20</span></div>
          <input
            className="range"
            type="range"
            min={1}
            max={20}
            value={localPointer}
            onChange={(event) => setLocalPointer(Number(event.target.value))}
            onPointerUp={() => onPointerSpeed(localPointer)}
            onKeyUp={() => onPointerSpeed(localPointer)}
          />
          <p className="hint">这是 Windows 指针倍率，不会改变硬件 DPI。</p>
        </section>

        <section className="panel">
          <PanelHeading title="Logitech" action={<button className="text-button" onClick={onRefresh}>重新扫描</button>} />
          <div className="stack">
            {devices.map((device) => (
              <LogitechCard key={device.id} device={device} onDpi={onDpi} />
            ))}
          </div>
        </section>
      </div>
    </div>
  )
}

function LogitechCard({ device, onDpi }: { device: LogitechDevice; onDpi: (id: string, dpi: number) => void }) {
  const [dpi, setDpi] = useState(device.dpi ?? 1600)
  useEffect(() => {
    if (device.dpi) setDpi(device.dpi)
  }, [device.dpi])
  if (!device.available) {
    return <EmptyState text={device.error ?? '未检测到 Logitech HID++ 设备'} />
  }

  return (
    <div className="control-card">
      <div className="card-heading">
        <div className="mouse-visual">
          <span />
          <span />
        </div>
        <div>
          <strong>{device.name}</strong>
          <span>HID++ · PID {device.productId.toString(16).toUpperCase()}</span>
        </div>
        <div className={`battery-pill ${device.charging ? 'charging' : ''}`}>
          {device.batteryPercent != null ? `${device.batteryPercent}%` : '电量未知'}
        </div>
      </div>
      <div className="range-label dpi-label">
        <span>硬件 DPI</span>
        <b>{dpi}</b>
      </div>
      <input
        className="range"
        type="range"
        min={200}
        max={25600}
        step={50}
        value={dpi}
        disabled={device.dpi == null || !device.writable}
        onChange={(event) => setDpi(Number(event.target.value))}
        onPointerUp={() => onDpi(device.id, dpi)}
        onKeyUp={() => onDpi(device.id, dpi)}
      />
      {device.dpi == null && <p className="hint">设备没有提供可读的 DPI 传感器。</p>}
      {device.dpi != null && !device.writable && (
        <p className="hint">{device.error ?? '当前为只读数据，DPI 暂不可写。'}</p>
      )}
    </div>
  )
}

function PerformancePage({
  vendor,
  hardware,
  gpu,
  processes,
  onMode,
  onGpu,
}: {
  vendor: VendorTelemetry | null
  hardware: HardwareProfile | null
  gpu: GpuControlSnapshot | null
  processes: ProcessInfo[]
  onMode: (mode: string) => void
  onGpu: (kind: string, value: number) => void
}) {
  const powerMinimum = Math.round(gpu?.powerMinimumWatts ?? 30)
  const powerMaximum = Math.round(gpu?.powerMaximumWatts ?? 300)
  const defaultPower = Math.round(
    gpu?.powerLimitWatts ?? gpu?.powerDefaultWatts ?? powerMaximum,
  )
  const [power, setPower] = useState(defaultPower)
  const [fan, setFan] = useState(gpu?.fanPercent ?? 60)
  const [coreOffset, setCoreOffset] = useState(gpu?.coreOffsetMhz ?? 0)
  const [memoryOffset, setMemoryOffset] = useState(gpu?.memoryOffsetMhz ?? 0)

  useEffect(() => {
    setPower(Math.round(gpu?.powerLimitWatts ?? gpu?.powerDefaultWatts ?? powerMaximum))
    setFan(gpu?.fanPercent ?? 60)
    setCoreOffset(gpu?.coreOffsetMhz ?? 0)
    setMemoryOffset(gpu?.memoryOffsetMhz ?? 0)
  }, [gpu, powerMaximum])

  return (
    <div className="performance-layout">
      <section className="panel board-control-panel">
        <PanelHeading
          title="主板控制面板"
          action={<span className={`badge ${hardware?.controlAvailable ? '' : 'warn'}`}>
            {hardware?.panelName ?? '正在识别主板'}
          </span>}
        />
        <div className="board-identity">
          <div className="board-chip"><Icon name="cpu" /></div>
          <div>
            <strong>{hardware ? `${hardware.manufacturer} ${hardware.product}` : '读取 SMBIOS…'}</strong>
            <span>{hardware?.model || '当前设备'}</span>
            <em>BIOS {hardware?.biosVersion ?? '—'} · {hardware?.familyName ?? '—'}</em>
          </div>
        </div>
        <div className="capability-strip">
          {(hardware?.capabilities ?? []).map((capability) => (
            <span key={capability}>{capability}</span>
          ))}
        </div>
        {hardware?.controlAvailable ? (
          <>
            <div className="mode-grid compact-modes">
              <ModeButton title="办公" subtitle="低功耗 / 安静" icon="leaf" onClick={() => onMode('office')} />
              <ModeButton title="游戏" subtitle="性能优先" icon="gamepad" tone="mint" onClick={() => onMode('gaming')} />
              <ModeButton title="狂飙" subtitle="最高风扇与功率" icon="bolt" tone="orange" onClick={() => onMode('turbo')} />
            </div>
            <p className="hint warning-hint">狂飙模式可能显著增加温度和噪音。切换前请连接电源并保存工作。</p>
          </>
        ) : (
          <div className="control-gate">
            <Icon name="shield" />
            <div>
              <strong>厂商写入接口未放开</strong>
              <p>{hardware?.detail ?? '正在检测本地控制接口。'}</p>
              <span>
                已识别的主板会显示对应面板；只有通过验证的本地接口才允许修改风扇和功率。
              </span>
            </div>
          </div>
        )}
        <div className="sensor-grid">
          <Sensor label="CPU 温度" value={vendor?.cpuTemperature ? `${vendor.cpuTemperature}°C` : '—'} />
          <Sensor label="GPU 温度" value={vendor?.gpuTemperature ? `${vendor.gpuTemperature}°C` : '—'} />
          <Sensor label="CPU 风扇" value={vendor?.cpuFanRpm ? `${vendor.cpuFanRpm} RPM` : '—'} />
          <Sensor label="GPU 风扇" value={vendor?.gpuFanRpm ? `${vendor.gpuFanRpm} RPM` : '—'} />
        </div>
        {hardware?.detectedInterfaces.length ? (
          <p className="interface-note">
            检测到本地接口：{hardware.detectedInterfaces.join(' · ')}
          </p>
        ) : null}
        {vendor?.error && hardware?.controlAvailable === false && (
          <p className="hint">通用传感器不依赖厂商控制面板；写入能力将在接口验证后启用。</p>
        )}
      </section>

      <section className="panel gpu-control-panel">
        <PanelHeading
          title="GPU 调校 · Afterburner 类"
          action={<span className="badge">{gpu?.source ?? 'NVML'}</span>}
        />
        <div className="gpu-hero">
          <div>
            <span>当前显卡</span>
            <strong>{gpu?.name ?? '正在读取 NVIDIA GPU'}</strong>
            <em>{gpu?.error ?? '读取功耗、频率、风扇与温度'}</em>
          </div>
          <div className="gpu-live-values">
            <b>{gpu?.powerUsageWatts != null ? `${gpu.powerUsageWatts.toFixed(0)}W` : '—'}</b>
            <span>实时功耗</span>
          </div>
        </div>
        <div className="gpu-stat-grid">
          <Sensor label="核心频率" value={gpu?.graphicsClockMhz ? `${gpu.graphicsClockMhz} MHz` : '—'} />
          <Sensor label="显存频率" value={gpu?.memoryClockMhz ? `${gpu.memoryClockMhz} MHz` : '—'} />
          <Sensor label="GPU 温度" value={gpu?.temperatureC ? `${gpu.temperatureC}°C` : '—'} />
          <Sensor label="NVML 风扇" value={gpu?.fanPercent != null ? `${gpu.fanPercent}%` : '厂商 EC'} />
        </div>

        <div className="gpu-control-row">
          <div className="range-label">
            <span>功耗上限</span>
            <b>{power} W</b>
          </div>
          <input
            className="range"
            type="range"
            min={powerMinimum}
            max={powerMaximum}
            value={Math.min(powerMaximum, Math.max(powerMinimum, power))}
            disabled={!gpu?.powerWritable}
            onChange={(event) => setPower(Number(event.target.value))}
          />
          <button
            className="button"
            disabled={!gpu?.powerWritable}
            onClick={() => onGpu('power', power)}
          >
            管理员应用
          </button>
        </div>
        <div className="gpu-control-row">
          <div className="range-label">
            <span>固定风扇转速</span>
            <b>{fan}%</b>
          </div>
          <input
            className="range"
            type="range"
            min={30}
            max={100}
            value={fan}
            disabled={!gpu?.fanWritable}
            onChange={(event) => setFan(Number(event.target.value))}
          />
          <button
            className="button"
            disabled={!gpu?.fanWritable}
            onClick={() => onGpu('fan', fan)}
          >
            应用
          </button>
        </div>
        <div className="gpu-offset-grid">
          <div className="gpu-offset">
            <div className="range-label"><span>核心偏移</span><b>{coreOffset} MHz</b></div>
            <input
              className="range"
              type="range"
              min={-200}
              max={200}
              value={coreOffset}
              disabled={gpu?.coreOffsetMhz == null}
              onChange={(event) => setCoreOffset(Number(event.target.value))}
            />
            <button
              className="button wide"
              disabled={gpu?.coreOffsetMhz == null}
              onClick={() => onGpu('core-offset', coreOffset)}
            >
              应用核心偏移
            </button>
          </div>
          <div className="gpu-offset">
            <div className="range-label"><span>显存偏移</span><b>{memoryOffset} MHz</b></div>
            <input
              className="range"
              type="range"
              min={-500}
              max={500}
              value={memoryOffset}
              disabled={gpu?.memoryOffsetMhz == null}
              onChange={(event) => setMemoryOffset(Number(event.target.value))}
            />
            <button
              className="button wide"
              disabled={gpu?.memoryOffsetMhz == null}
              onClick={() => onGpu('memory-offset', memoryOffset)}
            >
              应用显存偏移
            </button>
          </div>
        </div>
        <p className="hint">
          写入项会启动一次 UAC 助手；笔记本驱动不支持的项目保持禁用。功耗范围来自 NVIDIA NVML。
        </p>
      </section>

      <section className="panel performance-process-panel">
        <PanelHeading title="内存占用最高进程" action={<span className="small-note">实时采样</span>} />
        <ProcessList processes={processes} expanded />
      </section>
    </div>
  )
}

function GpuUnlockPage({
  unlock,
  dlss,
  onRefresh,
  onExportReport,
  onOpenUnlockUpstream,
  onDownload,
  onCancelDownload,
  onChooseGame,
  onInstall,
  onUninstall,
  onOpenInstallation,
  onOpenDlssUpstream,
}: {
  unlock: GpuUnlockStatus | null
  dlss: DlssUnlockedStatus | null
  onRefresh: () => void
  onExportReport: () => Promise<void>
  onOpenUnlockUpstream: () => void
  onDownload: () => Promise<void>
  onCancelDownload: () => Promise<void>
  onChooseGame: () => Promise<string | null>
  onInstall: (path: string, proxyName: string) => Promise<void>
  onUninstall: (id: string) => Promise<void>
  onOpenInstallation: (id: string) => void
  onOpenDlssUpstream: () => void
}) {
  const [gamePath, setGamePath] = useState('')
  const [proxyName, setProxyName] = useState<'dxgi' | 'version'>('dxgi')
  const [localError, setLocalError] = useState('')
  const percent = dlss?.download.percent ?? 0

  const run = async (action: () => Promise<void>) => {
    setLocalError('')
    try {
      await action()
    } catch (error) {
      setLocalError(messageOf(error))
    }
  }

  return (
    <div className="unlock-layout">
      <section className="panel unlock-gpu-panel">
        <PanelHeading
          title="GPU Unlock Windows"
          action={<span className="badge warn">UEFI 预启动</span>}
        />
        <div className="unlock-hero">
          <div className={`unlock-mark ${unlock?.inventorySupported ? 'supported' : ''}`}>
            <Icon name="cpu" />
          </div>
          <div>
            <span>当前 GPU 指纹</span>
            <strong>{unlock?.name ?? '正在读取 PCI 身份'}</strong>
            <em>
              {unlock
                ? `PCI ${unlock.vendorId}:${unlock.deviceId} · SUB ${unlock.subsystemVendorId}:${unlock.subsystemDeviceId}`
                : 'Win32_VideoController'}
            </em>
          </div>
        </div>
        <div className="identity-grid">
          <span>系统</span><b>{unlock?.systemModel ?? '—'}</b>
          <span>驱动</span><b>{unlock?.driverVersion ?? '—'}</b>
          <span>匹配 Profile</span><b>{unlock?.matchedProfileId ?? '无'}</b>
          <span>Profile 状态</span><b>{unlock?.profileStatus ?? '—'}</b>
        </div>
        <div className="control-gate unlock-gate">
          <Icon name="shield" />
          <div>
            <strong>{unlock?.profileWriteEnabled ? 'Profile 已启用写入' : '硬件写入保持锁定'}</strong>
            <p>{unlock?.detail ?? '正在读取项目 profile registry。'}</p>
            <span>{unlock?.requirement}</span>
          </div>
        </div>
        <div className="unlock-source">
          <span>上游 {unlock?.upstreamVersion ?? '0.1.0'} · MIT</span>
          <div className="button-row">
            <button className="button" onClick={() => void run(onExportReport)}>
              <Icon name="file" /> 导出兼容报告
            </button>
            <button className="button" onClick={onOpenUnlockUpstream}>
              <Icon name="open" /> 项目主页
            </button>
          </div>
        </div>
        <p className="hint warning-hint">
          LocalDeck 不会用用户态寄存器写入冒充 UEFI 解锁。只有精确 profile、预写值校验和恢复路径都满足时，
          上游工具才允许硬件写入。
        </p>
      </section>

      <section className="panel dlss-panel">
        <PanelHeading
          title="DLSS 5 / 神经渲染解锁"
          action={<span className="badge">DLSS Unlocked {dlss?.version ?? 'NR-v0.9.86'}</span>}
        />
        <div className="package-card">
          <div>
            <span>独立部署包</span>
            <strong>{formatBytes(dlss?.download.totalBytes ?? 629540064)}</strong>
            <em>{dlss?.packageReady ? '已下载并通过启动校验' : '首次使用需要下载'}</em>
          </div>
          <div className={`package-state ${dlss?.packageReady ? 'ready' : ''}`}>
            {dlss?.packageReady ? 'READY' : dlss?.download.running ? 'DOWNLOADING' : 'NOT CACHED'}
          </div>
        </div>
        {dlss?.download.running && (
          <div className="unlock-progress">
            <div><span style={{ width: `${Math.max(2, percent)}%` }} /></div>
            <em>{percent.toFixed(1)}% · {formatBytes(dlss.download.receivedBytes)} / {formatBytes(dlss.download.totalBytes)}</em>
          </div>
        )}
        <div className="button-row package-actions">
          <button
            className="button primary"
            disabled={dlss?.download.running || dlss?.busy || dlss?.packageReady}
            onClick={() => void run(onDownload)}
          >
            <Icon name="download" /> 下载并校验
          </button>
          <button
            className="button"
            disabled={!dlss?.download.running}
            onClick={() => void run(onCancelDownload)}
          >
            取消下载
          </button>
          <button className="button" onClick={onRefresh}>刷新状态</button>
          <button className="button" onClick={onOpenDlssUpstream}>项目主页</button>
        </div>

        <div className="dlss-install-form">
          <label className="path-field">
            <span>游戏主 exe 所在目录</span>
            <div className="folder-picker">
              <input value={gamePath} readOnly placeholder="选择包含游戏主 exe 的目录" />
              <button
                className="button"
                onClick={() => void run(async () => {
                  const selected = await onChooseGame()
                  if (selected) setGamePath(selected)
                })}
              >
                选择
              </button>
            </div>
          </label>
          <div className="proxy-choice">
            <span>代理 DLL</span>
            <div className="segmented">
              <button className={proxyName === 'dxgi' ? 'active' : ''} onClick={() => setProxyName('dxgi')}>dxgi.dll</button>
              <button className={proxyName === 'version' ? 'active' : ''} onClick={() => setProxyName('version')}>version.dll</button>
            </div>
          </div>
          <button
            className="button primary wide"
            disabled={!dlss?.packageReady || !gamePath || dlss?.busy}
            onClick={() => void run(() => onInstall(gamePath, proxyName))}
          >
            {dlss?.busy ? dlss.operation || '处理中…' : '部署到游戏目录'}
          </button>
        </div>
        {(localError || dlss?.error) && <p className="error-box">{localError || dlss?.error}</p>}
        <p className="hint">
          部署前会备份同名文件，卸载时逐文件恢复。仅用于支持 DLSS 的单机游戏，不建议用于带反作弊的多人游戏。
        </p>

        <div className="dlss-installations">
          <div className="panel-heading">
            <h2>已部署游戏</h2>
            <span className="small-note">{dlss?.installations.length ?? 0} INSTALLS</span>
          </div>
          {(dlss?.installations ?? []).map((installation) => (
            <div className="dlss-installation-row" key={installation.id}>
              <div className="device-icon"><Icon name="gamepad" /></div>
              <div>
                <strong>{installation.displayName}</strong>
                <span>{installation.gamePath}</span>
                <em>{installation.version} · {installation.proxyName}.dll</em>
              </div>
              <button className="button" onClick={() => onOpenInstallation(installation.id)}>打开</button>
              <button
                className="button danger-quiet"
                disabled={dlss?.busy}
                onClick={() => void run(() => onUninstall(installation.id))}
              >
                还原
              </button>
            </div>
          ))}
          {!dlss?.installations.length && (
            <EmptyState text="还没有由 LocalDeck 部署的 DLSS Unlocked 游戏目录" />
          )}
        </div>
      </section>
    </div>
  )
}

function PluginCenterPage({
  catalog,
  onRefresh,
  onToggle,
  onComplete,
}: {
  catalog: PluginCatalog | null
  onRefresh: () => void
  onToggle: (id: string, enabled: boolean) => Promise<void>
  onComplete: () => Promise<void>
}) {
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const supported = catalog?.capabilities.filter((item) => item.status === 'supported').length ?? 0
  const plugins = catalog?.plugins.length ?? 0

  const toggle = async (id: string, enabled: boolean) => {
    setBusy(id)
    setError('')
    try {
      await onToggle(id, enabled)
    } catch (cause) {
      setError(messageOf(cause))
    } finally {
      setBusy('')
    }
  }

  return (
    <div className="plugin-center">
      <section className="panel detection-hero">
        <div className="detection-mark"><Icon name="shield" /></div>
        <div>
          <span>FIRST-RUN CAPABILITY CHECK</span>
          <h2>先确认这台电脑能做什么</h2>
          <p>
            LocalDeck 已检测系统、显示器、主板、NVIDIA GPU、Logitech 和本地模型环境。
            只启用需要的插件包，未启用的模块不会进入主界面。
          </p>
        </div>
        <div className="detection-stats">
          <b>{supported}</b><span>可用能力</span>
          <b>{plugins}</b><span>可选插件</span>
        </div>
        <button className="button" onClick={onRefresh}>重新检测</button>
      </section>

      {!catalog && <EmptyState text="正在执行本机能力检测…" />}
      {catalog && (
        <>
          <section className="panel">
            <PanelHeading
              title="本机能力报告"
              action={<span className="small-note">{formatTime(catalog.detectedAt)}</span>}
            />
            <div className="capability-report">
              {catalog.capabilities.map((item) => (
                <div className={`capability-row status-${item.status}`} key={item.id}>
                  <span className="capability-indicator" />
                  <div>
                    <strong>{item.name}</strong>
                    <span>{item.detail}</span>
                  </div>
                  <em>{capabilityLabel(item.status)}</em>
                </div>
              ))}
            </div>
          </section>

          <section className="panel">
            <PanelHeading
              title="可选插件包"
              action={<span className="badge">按需启用</span>}
            />
            <div className="plugin-grid">
              {catalog.plugins.map((plugin) => (
                <article
                  className={`plugin-card ${plugin.enabled ? 'enabled' : ''} ${plugin.supported ? '' : 'unsupported'}`}
                  key={plugin.id}
                >
                  <div className="plugin-card-head">
                    <div className="plugin-icon"><Icon name={pluginIcon(plugin.id)} /></div>
                    <div>
                      <strong>{plugin.name}</strong>
                      <span>{plugin.delivery === 'on-demand' ? '按需下载资源' : '轻量启用'}</span>
                    </div>
                    <em>{plugin.enabled ? '已启用' : plugin.supported ? '可启用' : '不支持'}</em>
                  </div>
                  <p>{plugin.description}</p>
                  <div className="plugin-includes">
                    {plugin.includes.map((item) => <span key={item}>{item}</span>)}
                  </div>
                  <div className="plugin-detail">{plugin.detail}</div>
                  <button
                    className={`button ${plugin.enabled ? 'danger-quiet' : 'primary'}`}
                    disabled={busy === plugin.id || (!plugin.supported && !plugin.enabled)}
                    onClick={() => void toggle(plugin.id, !plugin.enabled)}
                  >
                    {busy === plugin.id
                      ? '处理中…'
                      : plugin.enabled
                        ? '停用并隐藏'
                        : plugin.delivery === 'on-demand'
                          ? '启用插件'
                          : '安装到工具箱'}
                  </button>
                </article>
              ))}
            </div>
          </section>
        </>
      )}

      {error && <p className="error-box">{error}</p>}
      {catalog && !catalog.onboardingCompleted && (
        <div className="onboarding-finish">
          <div>
            <strong>检测和选择已完成</strong>
            <span>之后仍可在“系统与模型 → 能力检测与插件”中增删插件包。</span>
          </div>
          <button className="button primary" onClick={() => void onComplete()}>
            进入 LocalDeck
          </button>
        </div>
      )}
    </div>
  )
}

function capabilityLabel(status: FeatureCapability['status']) {
  return ({
    supported: '支持',
    limited: '部分支持',
    unavailable: '不可用',
    'on-demand': '按需下载',
  } as Record<FeatureCapability['status'], string>)[status]
}

function pluginIcon(id: string): IconName {
  if (id === 'hardware') return 'cpu'
  if (id === 'system-tools') return 'trash'
  if (id === 'automation') return 'mouse'
  if (id === 'ai-workbench') return 'brain'
  if (id === 'game-enhancements') return 'gamepad'
  return 'grid'
}

function NetworkPage({
  proxy,
  onMode,
  onProxy,
  onRefresh,
  onStart,
  onStop,
  onSelectNode,
  onImport,
}: {
  proxy: ProxyStatus | null
  onMode: (mode: string) => void
  onProxy: (enabled: boolean) => void
  onRefresh: () => void
  onStart: () => void
  onStop: () => void
  onSelectNode: (name: string) => void
  onImport: () => void
}) {
  return (
    <div className="two-column">
      <section className="panel network-hero">
        <PanelHeading title="流量转发接口" action={<button className="text-button" onClick={onRefresh}>刷新</button>} />
        <div className="network-orb" data-online={Boolean(proxy?.running)}>
          <Icon name="network" />
          <strong>{proxy?.running ? 'PROXY ONLINE' : 'PROXY OFFLINE'}</strong>
          <span>{proxy?.running ? `${proxy.listenAddress}:${proxy.port}` : '混合 HTTP / SOCKS5'}</span>
        </div>
        <div className="network-facts">
          <div><span>服务状态</span><b>{proxy?.running ? '运行中' : '已停止'}</b></div>
          <div><span>当前节点</span><b>{proxy?.selectedNode ?? 'DIRECT'}</b></div>
          <div><span>配置文件</span><b>{proxy?.profilePath ? '已导入' : '未导入'}</b></div>
        </div>
        <div className="proxy-button-row">
          <button className="button primary" onClick={proxy?.running ? onStop : onStart}>
            {proxy?.running ? '停止代理' : '启动代理'}
          </button>
          <button className="button" onClick={onImport}>导入 YAML</button>
        </div>
        {proxy?.error && <p className="error-box">{proxy.error}</p>}
      </section>

      <div className="stack">
        <section className="panel">
          <PanelHeading title="系统代理 · 默认关闭" action={<Toggle checked={proxy?.systemProxyEnabled ?? false} onChange={onProxy} />} />
          <div className="proxy-address">
            <Icon name="link" />
            <div><span>当前系统代理</span><strong>{proxy?.systemProxyEnabled ? `127.0.0.1:${proxy.port}` : '未启用'}</strong></div>
          </div>
          <p className="hint">
            LocalDeck 默认只启动本地 HTTP/SOCKS5 流量转发，不修改 Windows 系统代理；
            需要接管全局流量时再手动开启。
          </p>
        </section>

        <section className="panel">
          <PanelHeading title="路由模式" />
          <div className="segmented">
            {['rule', 'global', 'direct'].map((mode) => (
              <button
                key={mode}
                className={proxy?.mode === mode ? 'active' : ''}
                onClick={() => onMode(mode)}
              >
                {modeLabel(mode)}
              </button>
            ))}
          </div>
          <div className="node-list">
            {(proxy?.nodes ?? []).map((node) => (
              <button
                key={node.name}
                className={`node-row ${proxy?.selectedNode === node.name ? 'active' : ''}`}
                onClick={() => node.supported && onSelectNode(node.name)}
                disabled={!node.supported}
                title={node.warning ?? node.type}
              >
                <span className="node-type">{node.type.toUpperCase()}</span>
                <strong>{node.name}</strong>
                <em>{node.supported ? '可直连' : '协议暂不支持'}</em>
              </button>
            ))}
            {!proxy?.nodes.length && <EmptyState text="导入代理 YAML 后会显示节点" />}
          </div>
        </section>
      </div>
    </div>
  )
}

function ClipboardPage({
  entries,
  onRefresh,
  onCapture,
  onCopy,
  onPin,
  onDelete,
  onClear,
  onAddSnippet,
}: {
  entries: ClipboardEntry[]
  onRefresh: () => void
  onCapture: () => void
  onCopy: (id: number) => Promise<void>
  onPin: (id: number) => Promise<void>
  onDelete: (id: number) => Promise<void>
  onClear: (includePinned: boolean) => Promise<void>
  onAddSnippet: (text: string) => Promise<void>
}) {
  const [filter, setFilter] = useState('')
  const [snippet, setSnippet] = useState('')
  const visible = entries.filter((entry) =>
    `${entry.preview} ${entry.kind}`.toLowerCase().includes(filter.toLowerCase()),
  )
  return (
    <>
      <section className="clipboard-toolbar panel">
        <div className="search-box">
          <Icon name="search" />
          <input value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="筛选本地历史…" />
        </div>
        <button className="button primary" onClick={onCapture}><Icon name="camera" /> 截图并贴图</button>
        <button className="button" onClick={onRefresh}>刷新</button>
        <button className="button danger-quiet" onClick={() => void onClear(false)}>清理未固定</button>
      </section>

      <section className="panel snippet-panel">
        <div>
          <strong>固定常用片段</strong>
          <span>保存在本机 DPAPI 加密数据库中，可随时复制。</span>
        </div>
        <div className="snippet-form">
          <input
            value={snippet}
            onChange={(event) => setSnippet(event.target.value)}
            placeholder="例如邮箱、常用回复、命令…"
            onKeyDown={(event) => {
              if (event.key === 'Enter' && snippet.trim()) {
                void onAddSnippet(snippet.trim())
                setSnippet('')
              }
            }}
          />
          <button
            className="button"
            disabled={!snippet.trim()}
            onClick={() => {
              void onAddSnippet(snippet.trim())
              setSnippet('')
            }}
          >
            <Icon name="pin" /> 固定
          </button>
        </div>
      </section>

      <div className="clipboard-grid">
        {visible.map((entry) => (
          <article className={`clip-card ${entry.pinned ? 'pinned' : ''}`} key={entry.id}>
            <div className="clip-meta">
              <span className="clip-kind"><Icon name={clipIcon(entry.kind)} /> {entry.kind.toUpperCase()}</span>
              <time>{formatTime(entry.createdAt)}</time>
            </div>
            <p className="clip-preview">{entry.preview || '空内容'}</p>
            <div className="clip-footer">
              <span>{entry.characterCount.toLocaleString()} 字符</span>
              <div>
                <button title="复制" onClick={() => void onCopy(entry.id)}><Icon name="copy" /></button>
                <button title="固定" className={entry.pinned ? 'active' : ''} onClick={() => void onPin(entry.id)}><Icon name="pin" /></button>
                <button title="删除" onClick={() => void onDelete(entry.id)}><Icon name="trash" /></button>
              </div>
            </div>
          </article>
        ))}
      </div>
      {visible.length === 0 && <EmptyState text="剪贴板历史为空。复制任意文本或图片后会出现在这里。" />}
    </>
  )
}

function FilesPage({
  status,
  roots,
  onStatus,
  onRootsChange,
  onChooseRoot,
  onReindex,
  onStop,
}: {
  status: FileIndexStatus | null
  roots: string[]
  onStatus: (status: FileIndexStatus) => void
  onRootsChange: (roots: string[]) => Promise<void>
  onChooseRoot: () => Promise<void>
  onReindex: () => Promise<void>
  onStop: () => Promise<void>
}) {
  const [query, setQuery] = useState('')
  const [results, setResults] = useState<FileSearchResult[]>([])
  const inputRef = useRef<HTMLInputElement>(null)

  useEffect(() => {
    inputRef.current?.focus()
  }, [])

  useEffect(() => {
    if (!query.trim()) {
      setResults([])
      return
    }

    const timeout = window.setTimeout(() => {
      invoke<FileSearchResult[]>('searchFiles', { query, limit: 100 })
        .then(setResults)
        .catch(() => setResults([]))
    }, 220)
    return () => window.clearTimeout(timeout)
  }, [query])

  return (
    <>
      <section className="file-search panel">
        <div className="search-box large">
          <Icon name="search" />
          <input
            ref={inputRef}
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="搜索本机文件名，例如 report.pdf…"
          />
          {query && <button onClick={() => setQuery('')}><Icon name="close" /></button>}
        </div>
        <div className="index-stats">
          <div>
            <span>{status?.indexing ? '索引进行中' : '索引就绪'}</span>
            <strong>{(status?.fileCount ?? 0).toLocaleString()}</strong>
            <em>个文件</em>
          </div>
          <div className="index-actions">
            <button className="button" onClick={() => void onChooseRoot()}>添加目录</button>
            <button className="button primary" onClick={() => void onReindex()}>重建索引</button>
            {status?.indexing && <button className="button danger-quiet" onClick={() => void onStop()}>停止</button>}
          </div>
        </div>
        <div className="root-chips">
          {roots.map((root) => (
            <span key={root}>
              {root}
              <button
                title="移除目录"
                onClick={() => void onRootsChange(roots.filter((item) => item !== root))}
              >
                <Icon name="close" />
              </button>
            </span>
          ))}
        </div>
        {status?.error && <p className="error-box">{status.error}</p>}
      </section>

      <section className="file-results">
        {results.map((file) => (
          <article className="file-row" key={file.path} onDoubleClick={() => void invoke('openPath', { path: file.path })}>
            <div className="file-icon"><Icon name={fileIcon(file.name)} /></div>
            <div className="file-copy">
              <strong>{highlight(file.name, query)}</strong>
              <span>{file.directory}</span>
            </div>
            <div className="file-meta">
              <span>{formatBytes(file.size)}</span>
              <time>{formatTime(file.modified)}</time>
            </div>
            <div className="file-actions">
              <button title="打开" onClick={async () => { await invoke('openPath', { path: file.path }); onStatus(await invoke('getFileIndexStatus')) }}><Icon name="open" /></button>
              <button title="在资源管理器中显示" onClick={async () => { await invoke('revealPath', { path: file.path }); onStatus(await invoke('getFileIndexStatus')) }}><Icon name="folder" /></button>
            </div>
          </article>
        ))}
        {query && results.length === 0 && <EmptyState text="没有匹配文件。索引可能仍在建立，请稍后重试。" />}
        {!query && (
          <div className="search-empty">
            <div className="search-glyph"><Icon name="search" /></div>
            <h2>输入文件名开始搜索</h2>
            <p>所有索引数据保存在 LocalDeck 本地数据库中。</p>
          </div>
        )}
      </section>
    </>
  )
}

function AudioPage({ sessions, onRefresh }: { sessions: AudioSession[]; onRefresh: () => void }) {
  const [volumes, setVolumes] = useState<Record<number, number>>({})
  useEffect(() => {
    setVolumes((current) => {
      const next = { ...current }
      sessions.forEach((session) => {
        if (next[session.processId] == null) next[session.processId] = session.volumePercent
      })
      return next
    })
  }, [sessions])

  const commit = async (processId: number, value: number, muted?: boolean) => {
    await invoke('setAudioVolume', { processId, value, ...(muted == null ? {} : { muted }) })
  }

  return (
    <section className="panel">
      <PanelHeading title="当前播放会话" action={<button className="text-button" onClick={onRefresh}>刷新</button>} />
      <div className="audio-list">
        {sessions.map((session) => {
          const value = volumes[session.processId] ?? session.volumePercent
          return (
            <div className="audio-row" key={session.processId}>
              <div className="audio-avatar">{session.name.slice(0, 2).toUpperCase()}</div>
              <div className="audio-name"><strong>{session.name}</strong><span>PID {session.processId}</span></div>
              <button
                className={`icon-button ${session.muted ? 'muted' : ''}`}
                onClick={() => void commit(session.processId, value, !session.muted)}
              >
                <Icon name={session.muted ? 'mute' : 'volume'} />
              </button>
              <input
                className="range"
                type="range"
                min={0}
                max={100}
                value={value}
                onChange={(event) => setVolumes((current) => ({ ...current, [session.processId]: Number(event.target.value) }))}
                onPointerUp={() => void commit(session.processId, value)}
              />
              <b>{Math.round(value)}%</b>
            </div>
          )
        })}
        {sessions.length === 0 && <EmptyState text="当前没有正在播放声音的应用" />}
      </div>
    </section>
  )
}

function LauncherPage() {
  const [query, setQuery] = useState('')
  const [items, setItems] = useState<ExecutableItem[]>([])
  const [expanded, setExpanded] = useState<Record<string, boolean>>({})
  const [error, setError] = useState('')
  const load = useCallback(async (value: string) => {
    try {
      setItems(await invoke<ExecutableItem[]>('getExecutables', { query: value, limit: 1200 }))
      setError('')
    } catch (cause) {
      setError(messageOf(cause))
    }
  }, [])
  useEffect(() => {
    const timeout = window.setTimeout(() => void load(query), query ? 180 : 0)
    return () => window.clearTimeout(timeout)
  }, [load, query])
  const groups = [
    { id: 'game', label: '游戏', icon: 'gamepad' as IconName },
    { id: 'application', label: '应用', icon: 'grid' as IconName },
    { id: 'utility', label: '工具与非常用程序', icon: 'code' as IconName },
  ]
  return (
    <div className="launcher-page">
      <section className="launcher-search panel">
        <div className="search-box large">
          <Icon name="rocket" />
          <input
            autoFocus
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="搜索所有已索引 EXE…"
          />
        </div>
        <span className="small-note">游戏优先 · 使用次数自动排序</span>
      </section>
      {error && <p className="error-box">{error}</p>}
      {groups.map((group) => {
        const groupItems = items.filter((item) => item.kind === group.id)
        if (!groupItems.length) return null
        const visibleItems = expanded[group.id] || query
          ? groupItems
          : groupItems.slice(0, 48)
        return (
          <section className="panel launcher-group" key={group.id}>
            <PanelHeading
              title={`${group.label} · ${groupItems.length}`}
              action={groupItems.length > 48 && !query ? (
                <button
                  className="text-button"
                  onClick={() => setExpanded({ ...expanded, [group.id]: !expanded[group.id] })}
                >
                  {expanded[group.id] ? '收起' : `显示全部 ${groupItems.length}`}
                </button>
              ) : undefined}
            />
            <div className="launcher-grid">
              {visibleItems.map((item) => (
                <article className={`launcher-tile ${item.pinned ? 'pinned' : ''}`} key={item.path}>
                  <div className="launcher-icon"><Icon name={group.icon} /></div>
                  <div className="launcher-copy" title={item.path}>
                    <strong>{item.name}</strong>
                    <span>{item.path}</span>
                  </div>
                  <div className="launcher-meta">
                    <span>{item.usageCount ? `${item.usageCount} 次` : formatBytes(item.size)}</span>
                    <div>
                      <button title="固定" onClick={async () => { await invoke('toggleExecutablePin', { item }); void load(query) }}>
                        <Icon name="pin" />
                      </button>
                      <button title="启动" onClick={async () => { await invoke('launchExecutable', { item }); void load(query) }}>
                        <Icon name="play" />
                      </button>
                    </div>
                  </div>
                </article>
              ))}
            </div>
          </section>
        )
      })}
      {!items.length && <EmptyState text="文件索引还在建立，或没有匹配的 EXE。" />}
    </div>
  )
}

function UninstallerPage() {
  const [apps, setApps] = useState<InstalledApp[]>([])
  const [filter, setFilter] = useState('')
  const [selected, setSelected] = useState<InstalledApp | null>(null)
  const [leftovers, setLeftovers] = useState<AppLeftover[]>([])
  const [message, setMessage] = useState('')
  const load = useCallback(async () => {
    try {
      setApps(await invoke<InstalledApp[]>('getInstalledApps'))
    } catch (error) {
      setMessage(messageOf(error))
    }
  }, [])
  useEffect(() => { void load() }, [load])
  const visible = apps.filter((app) =>
    `${app.name} ${app.publisher} ${app.version}`.toLowerCase().includes(filter.toLowerCase()),
  )
  const scan = async (app: InstalledApp) => {
    setSelected(app)
    try {
      setLeftovers(await invoke<AppLeftover[]>('scanLeftovers', { app }))
    } catch (error) {
      setMessage(messageOf(error))
    }
  }
  return (
    <div className="two-column uninstaller-layout">
      <section className="panel">
        <PanelHeading title={`已安装程序 · ${apps.length}`} action={<button className="text-button" onClick={() => void load()}>刷新</button>} />
        <div className="search-box uninstaller-search">
          <Icon name="search" />
          <input value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="筛选应用、发布者…" />
        </div>
        <div className="uninstall-list">
          {visible.slice(0, 400).map((app) => (
            <button className={`uninstall-row ${selected?.registryPath === app.registryPath ? 'active' : ''}`} key={app.registryPath} onClick={() => void scan(app)}>
              <span className="uninstall-icon">{app.name.slice(0, 2).toUpperCase()}</span>
              <span><strong>{app.name}</strong><em>{app.publisher || '未知发布者'} · {app.version || '无版本'}</em></span>
              <b>{formatBytes(app.estimatedBytes)}</b>
            </button>
          ))}
        </div>
      </section>
      <div className="stack">
        <section className="panel">
          <PanelHeading title="卸载与残留" action={selected && <span className="badge">{selected.scope}</span>} />
          {!selected ? <EmptyState text="从左侧选择一个程序" /> : (
            <>
              <h3>{selected.name}</h3>
              <p className="detail-copy">{selected.installLocation || '没有登记安装目录'}</p>
              <div className="button-row">
                <button className="button danger-quiet" onClick={() => {
                  if (window.confirm(`启动“${selected.name}”的卸载程序？`)) {
                    void invoke('launchUninstaller', { app: selected })
                  }
                }}>启动卸载</button>
                <button className="button" onClick={() => void scan(selected)}>扫描残留</button>
              </div>
            </>
          )}
          {message && <p className={message.includes('失败') ? 'error-box' : 'hint'}>{message}</p>}
        </section>
        <section className="panel">
          <PanelHeading title="残留候选" action={<span className="small-note">删除前自动备份</span>} />
          <div className="leftover-list">
            {leftovers.map((item) => (
              <div className="leftover-row" key={`${item.kind}-${item.target}`}>
                <Icon name={item.isRegistry ? 'settings' : 'folder'} />
                <div><strong>{item.kind === 'registry' ? '注册表' : '目录'}</strong><span>{item.target}</span><em>{item.description}</em></div>
                <button className="button danger-quiet" onClick={async () => {
                  if (!window.confirm(`处理这个残留项？\n${item.target}`)) return
                  try {
                    const result = item.isDirectory
                      ? await invoke<{ path: string }>('quarantineDirectory', { item })
                      : await invoke<{ backup: string }>('deleteRegistryLeftover', { item })
                    setMessage(`已处理并备份：${'path' in result ? result.path : result.backup}`)
                    setLeftovers(leftovers.filter((entry) => entry !== item))
                  } catch (error) { setMessage(messageOf(error)) }
                }}>{item.isDirectory ? '隔离' : '备份后删除'}</button>
              </div>
            ))}
            {!leftovers.length && <EmptyState text="暂无扫描结果。卸载后再次扫描更准确。" />}
          </div>
        </section>
      </div>
    </div>
  )
}

function DiskAnalyzerPage() {
  const [drives, setDrives] = useState<string[]>(['C:\\'])
  const [root, setRoot] = useState('C:\\')
  const [status, setStatus] = useState<DiskAnalysisStatus | null>(null)
  const start = async () => {
    setStatus(await invoke<DiskAnalysisStatus>('startDiskAnalysis', { root }))
  }
  useEffect(() => {
    void invoke<string[]>('getDiskRoots').then((roots) => {
      setDrives(roots)
      if (roots.length > 0) setRoot(roots[0])
    })
    void invoke<DiskAnalysisStatus>('getDiskAnalysis').then(setStatus)
  }, [])
  useEffect(() => {
    if (!status?.running) return
    const timer = window.setInterval(() => {
      void invoke<DiskAnalysisStatus>('getDiskAnalysis').then(setStatus)
    }, 800)
    return () => window.clearInterval(timer)
  }, [status?.running])
  const total = status?.totalBytes || 1
  return (
    <div className="stack">
      <section className="panel disk-toolbar">
        <select value={root} onChange={(event) => setRoot(event.target.value)}>
          {drives.map((drive) => <option key={drive}>{drive}</option>)}
        </select>
        <button className="button primary" onClick={() => void start()} disabled={status?.running}>开始扫描</button>
        <button className="button" onClick={async () => setStatus(await invoke<DiskAnalysisStatus>('cancelDiskAnalysis'))} disabled={!status?.running}>停止</button>
        <span>{status?.running ? `正在扫描 ${status.currentPath ?? ''}` : status?.totalBytes ? `${formatBytes(status.totalBytes)} / ${status.fileCount.toLocaleString()} 个文件` : '按需扫描，未运行时不占内存'}</span>
      </section>
      <section className="panel">
        <PanelHeading title="空间占用图" action={<span className="small-note">扫描中可停止</span>} />
        <div className="treemap">
          {(status?.items ?? []).map((item, index) => {
            const percent = Math.max(1.5, item.sizeBytes / total * 100)
            return (
              <button
                key={item.path}
                className={`treemap-item color-${index % 6}`}
                style={{ flexGrow: percent }}
                onClick={() => void invoke('openPath', { path: item.path })}
                title={`${item.path}\n${formatBytes(item.sizeBytes)} · ${item.fileCount} 个文件`}
              >
                <strong>{item.name}</strong>
                <span>{formatBytes(item.sizeBytes)}</span>
              </button>
            )
          })}
          {!status?.items.length && <EmptyState text="选择磁盘后开始扫描" />}
        </div>
        {status?.error && <p className="error-box">{status.error}</p>}
      </section>
    </div>
  )
}

function DriverPage() {
  const [drivers, setDrivers] = useState<DriverInfo[]>([])
  const [updates, setUpdates] = useState<DriverUpdate[]>([])
  const [filter, setFilter] = useState('')
  const [checking, setChecking] = useState(false)
  const [message, setMessage] = useState('')
  useEffect(() => {
    void invoke<DriverInfo[]>('getDrivers').then(setDrivers).catch((error) => setMessage(messageOf(error)))
  }, [])
  const check = async () => {
    setChecking(true)
    setMessage('')
    try {
      setUpdates(await invoke<DriverUpdate[]>('checkDriverUpdates'))
    } catch (error) {
      setMessage(`Windows Update 驱动查询失败：${messageOf(error)}`)
    } finally {
      setChecking(false)
    }
  }
  const visible = drivers.filter((driver) =>
    `${driver.deviceName} ${driver.manufacturer} ${driver.class} ${driver.version}`
      .toLowerCase().includes(filter.toLowerCase()),
  )
  return (
    <div className="two-column">
      <section className="panel">
        <PanelHeading title={`已安装驱动 · ${drivers.length}`} />
        <div className="search-box"><Icon name="search" /><input value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="筛选设备、厂商、类…" /></div>
        <div className="driver-list">
          {visible.slice(0, 300).map((driver) => (
            <div className="driver-row" key={`${driver.deviceId}-${driver.infName}`}>
              <div><strong>{driver.deviceName}</strong><span>{driver.manufacturer} · {driver.class}</span></div>
              <b>{driver.version || '—'}</b>
            </div>
          ))}
        </div>
      </section>
      <section className="panel">
        <PanelHeading title="Windows Update 驱动" action={<button className="button primary" onClick={() => void check()} disabled={checking}>{checking ? '查询中…' : '检查驱动更新'}</button>} />
        <p className="hint">只有点击按钮时才访问 Windows Update；LocalDeck 启动时不会联网查询。</p>
        <div className="update-list">
          {updates.map((update) => (
            <div className="update-row" key={update.title}>
              <Icon name="driver" />
              <div><strong>{update.title}</strong><span>{update.manufacturer} {update.model}</span></div>
              {update.rebootRequired && <span className="badge warn">需重启</span>}
            </div>
          ))}
          {!updates.length && !checking && <EmptyState text="点击检查后显示可用驱动更新" />}
        </div>
        {message && <p className="error-box">{message}</p>}
      </section>
    </div>
  )
}

function ModelCenterPage({
  settings,
  onSave,
}: {
  settings: AppSettings
  onSave: (settings: AppSettings, message?: string) => Promise<void>
}) {
  const [models, setModels] = useState<LocalModelInfo[]>([])
  const [engine, setEngine] = useState<LocalModelEngineInfo | null>(null)
  const [download, setDownload] = useState<DownloadStatus | null>(null)
  const [server, setServer] = useState<LocalServerStatus | null>(null)
  const [url, setUrl] = useState('')
  const [fileName, setFileName] = useState('')
  const [editingArgs, setEditingArgs] = useState<Record<string, string>>({})
  const [benchmark, setBenchmark] = useState('')
  const [message, setMessage] = useState('')
  const load = useCallback(async () => {
    try {
      const next = await invoke<LocalModelInfo[]>('getLocalModels')
      setModels(next)
      setEditingArgs(Object.fromEntries(next.map((model) => [model.path, model.customArguments])))
      setEngine(await invoke<LocalModelEngineInfo>('getLocalModelEngine'))
      setDownload(await invoke<DownloadStatus>('getModelDownloadStatus'))
    } catch (error) { setMessage(messageOf(error)) }
  }, [])
  useEffect(() => {
    void load()
  }, [load, settings.localModelDirectory, settings.localModelEngineDirectory])
  useEffect(() => {
    if (!download?.running) return
    const timer = window.setInterval(() => {
      void invoke<DownloadStatus>('getModelDownloadStatus').then(setDownload)
    }, 700)
    return () => window.clearInterval(timer)
  }, [download?.running])
  const clouds = [
    ['ChatGPT', 'chatgpt'], ['DeepSeek', 'deepseek'], ['Grok', 'grok'], ['Claude', 'claude'], ['Gemini', 'gemini'],
  ]
  return (
    <div className="stack">
      <section className="panel model-toolbar">
        <div>
          <strong>模型目录</strong>
          <span>{settings.localModelDirectory || '未配置'}</span>
        </div>
        <div>
          <strong>推理引擎</strong>
          <span>{engine?.directory || '未配置 llama-server.exe'}</span>
          {engine?.serverAvailable && (
            <em className={engine.ownedByLocalDeck ? 'engine-owned' : 'engine-external'}>
              {engine.ownedByLocalDeck ? 'LocalDeck 自有引擎' : '外部引擎，可迁移后卸载原程序'}
            </em>
          )}
        </div>
        <div className="model-toolbar-actions">
          <button className="button" onClick={async () => {
            const result = await invoke<{ path: string | null }>('chooseLocalModelDirectory')
            if (result.path) {
              await onSave({ ...settings, localModelDirectory: result.path }, '模型目录已更新')
            }
          }}>选择模型目录</button>
          <button className="button" onClick={async () => {
            const result = await invoke<{ path: string | null }>('chooseLocalModelEngine')
            if (result.path) {
              await onSave({ ...settings, localModelEngineDirectory: result.path }, 'llama.cpp 引擎目录已更新')
            }
          }}>选择引擎目录</button>
          {engine?.serverAvailable && !engine.ownedByLocalDeck && (
            <button className="button primary" onClick={async () => {
              setMessage('正在复制引擎，首次可能需要数分钟…')
              try {
                const adopted = await invoke<LocalModelEngineInfo>('adoptLocalModelEngine')
                setEngine(adopted)
                await onSave(
                  { ...settings, localModelEngineDirectory: adopted.directory ?? settings.localModelEngineDirectory },
                  '引擎已复制到 LocalDeck',
                )
                setMessage('引擎已迁移，可卸载原模型工具。')
              } catch (error) {
                setMessage(messageOf(error))
              }
            }}>复制引擎到 LocalDeck</button>
          )}
          <button className="button" onClick={() => void load()}>刷新模型</button>
        </div>
      </section>
      <div className="model-clouds">
        {clouds.map(([label, id]) => (
          <button key={id} className="cloud-model" onClick={async () => { await invoke('openCloudModel', { id }); }}>
            <span>{label.slice(0, 1)}</span><strong>{label}</strong><em>Chrome 新窗口</em>
          </button>
        ))}
      </div>
      <section className="panel">
        <PanelHeading title="下载 GGUF" action={<span className="badge">显式网络操作</span>} />
        <div className="model-download-row">
          <input value={url} onChange={(event) => setUrl(event.target.value)} placeholder="https://…/model.gguf" />
          <input value={fileName} onChange={(event) => setFileName(event.target.value)} placeholder="model.gguf" />
          <button className="button primary" disabled={download?.running || !url || !fileName} onClick={async () => {
            try { setDownload(await invoke<DownloadStatus>('startModelDownload', { url, fileName })) }
            catch (error) { setMessage(messageOf(error)) }
          }}>开始下载</button>
          {download?.running && <button className="button danger-quiet" onClick={async () => setDownload(await invoke<DownloadStatus>('cancelModelDownload'))}>停止</button>}
        </div>
        {download?.running && <div className="progress-track"><div style={{ width: `${download.percent}%` }} /></div>}
        {download && <p className="hint">{download.fileName} · {formatBytes(download.receivedBytes)} / {formatBytes(download.totalBytes)}</p>}
        {download?.error && <p className="error-box">{download.error}</p>}
      </section>
      <section className="panel">
        <PanelHeading title={`本地模型 · ${models.length}`} action={server?.running ? <span className="badge">服务端口 {server.port}</span> : undefined} />
        <div className="local-model-list">
          {models.map((model) => (
            <article className="local-model" key={model.path}>
              <div className="model-mark"><Icon name="brain" /></div>
              <div className="model-copy">
                <strong>{model.name}</strong>
                <span>{formatBytes(model.sizeBytes)} · {model.quantization || '未知量化'} · 估算 {model.estimatedTokensPerSecond.toFixed(1)} t/s</span>
                <input value={editingArgs[model.path] ?? ''} onChange={(event) => setEditingArgs({ ...editingArgs, [model.path]: event.target.value })} placeholder="自定义 llama-server 参数" />
              </div>
              <div className="model-actions">
                <button onClick={async () => { await invoke('saveModelArguments', { path: model.path, arguments: editingArgs[model.path] ?? '' }); setMessage('启动参数已保存') }}>保存参数</button>
                <button onClick={async () => {
                  setBenchmark('基准测试运行中…')
                  const result = await invoke<{ output: string }>('benchmarkModel', { path: model.path })
                  setBenchmark(result.output)
                }}>实测 TPS</button>
                <button className="primary-small" onClick={async () => {
                  try {
                    const next = await invoke<LocalServerStatus>('startModelServer', { path: model.path })
                    setServer(next)
                    if (next.running) await invoke('openUrl', { url: `http://127.0.0.1:${next.port}` })
                  } catch (error) { setMessage(messageOf(error)) }
                }}>启动</button>
              </div>
            </article>
          ))}
          {!models.length && <EmptyState text="模型目录中没有 GGUF 文件" />}
        </div>
        {(server?.error || message) && <p className="error-box">{server?.error || message}</p>}
        {benchmark && <pre className="log-output">{benchmark}</pre>}
      </section>
    </div>
  )
}

function WindowsLogPage() {
  const [hours, setHours] = useState(24)
  const [report, setReport] = useState<WindowsLogReport | null>(null)
  const [loading, setLoading] = useState(false)
  const [filter, setFilter] = useState('')
  const load = useCallback(async () => {
    setLoading(true)
    try { setReport(await invoke<WindowsLogReport>('getWindowsLogs', { hours })) }
    finally { setLoading(false) }
  }, [hours])
  useEffect(() => { void load() }, [load])
  const entries = (report?.entries ?? []).filter((item) =>
    `${item.provider} ${item.message} ${item.level}`.toLowerCase().includes(filter.toLowerCase()),
  )
  return (
    <div className="two-column log-layout">
      <section className="panel">
        <PanelHeading title="错误来源聚合" action={<select value={hours} onChange={(event) => setHours(Number(event.target.value))}><option value={24}>最近 24 小时</option><option value={72}>最近 3 天</option><option value={168}>最近 7 天</option></select>} />
        <button className="button primary" onClick={() => void load()} disabled={loading}>{loading ? '分析中…' : '开始分析'}</button>
        <div className="log-groups">
          {(report?.groups ?? []).map((group) => (
            <div className="log-group" key={`${group.logName}-${group.provider}`}>
              <div><strong>{group.provider || '未知来源'}</strong><span>{group.logName} · 最近 {formatTime(group.latest)}</span></div>
              <b>{group.count}</b>
              {group.errorCount > 0 && <em>{group.errorCount} 错误</em>}
            </div>
          ))}
        </div>
        {(report?.minidumps.length ?? 0) > 0 && (
          <div className="crash-dumps">
            <strong>检测到 {report!.minidumps.length} 个蓝屏转储</strong>
          </div>
        )}
      </section>
      <section className="panel">
        <PanelHeading title={`事件明细 · ${entries.length}`} />
        <div className="search-box"><Icon name="search" /><input value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="筛选来源和内容…" /></div>
        <div className="event-list">
          {entries.map((entry) => (
            <article className="event-row" key={`${entry.time}-${entry.provider}-${entry.eventId}`}>
              <div><span className={entry.level.includes('错误') ? 'level error' : 'level'}>{entry.level}</span><time>{formatTime(entry.time)}</time></div>
              <strong>{entry.provider} · {entry.eventId}</strong>
              <p>{entry.message}</p>
            </article>
          ))}
        </div>
      </section>
    </div>
  )
}

function ShortcutCenter({
  settings,
  registrations,
  onSave,
}: {
  settings: AppSettings
  registrations: HotkeyRegistration[]
  onSave: (settings: AppSettings) => Promise<void>
}) {
  const [draft, setDraft] = useState(settings)
  useEffect(() => setDraft(settings), [settings])
  const setHotkey = (key: keyof AppSettings['hotkeys'], value: string) => {
    setDraft({ ...draft, hotkeys: { ...draft.hotkeys, [key]: value } })
  }
  const fixed: Array<{ key: Exclude<keyof AppSettings['hotkeys'], 'custom'>; id: string; label: string; note: string }> = [
    { key: 'screenshot', id: 'screenshot', label: '区域截图', note: '与 Snipaste F1 对应，只复制截图' },
    { key: 'pinImage', id: 'pin-image', label: '贴图剪贴板图片', note: '与 Snipaste F3 对应，置顶显示' },
    { key: 'screenshotPin', id: 'screenshot-pin', label: '截图并贴图', note: '一次完成截图与置顶' },
    { key: 'clipboard', id: 'clipboard', label: '剪贴板历史', note: '打开本地剪贴板管理' },
    { key: 'search', id: 'search', label: '文件搜索', note: '聚焦本地文件索引' },
    { key: 'openPanel', id: 'panel', label: '打开 LocalDeck', note: '从任意应用唤出控制台' },
    { key: 'proxyToggle', id: 'proxy-toggle', label: '切换流量转发', note: '启动或停止本地接口，不修改系统代理' },
    { key: 'macroEditor', id: 'macro-editor', label: '打开宏编辑器', note: '在独立窗口中录制和编辑侧键宏' },
    { key: 'launcher', id: 'launcher', label: '打开快捷启动', note: '复用文件索引快速启动游戏和应用' },
    { key: 'overlay', id: 'overlay', label: '性能悬浮窗', note: '显示 CPU/GPU 使用率、温度、FPS 与设备电量' },
  ]

  return (
    <div className="two-column shortcut-layout">
      <section className="panel">
        <PanelHeading title="系统级快捷键" action={<span className="badge">全局生效</span>} />
        <div className="shortcut-editor">
          {fixed.map((item) => {
            const registration = registrations.find((entry) => entry.id === item.id)
            return (
              <div className="shortcut-edit-row" key={item.id}>
                <div className="shortcut-copy">
                  <strong>{item.label}</strong>
                  <span>{item.note}</span>
                </div>
                <input
                  value={draft.hotkeys[item.key]}
                  onChange={(event) => setHotkey(item.key, event.target.value)}
                  placeholder="例如 Ctrl+Alt+S"
                />
                <span className={`hotkey-state ${registration?.registered ? 'ok' : 'bad'}`}>
                  {registration?.registered ? '已注册' : registration?.error ?? '未设置'}
                </span>
              </div>
            )
          })}
        </div>
      </section>

      <div className="stack">
        <section className="panel">
          <PanelHeading title="自定义动作" action={<span className="small-note">快捷键控制整台电脑</span>} />
          <div className="custom-hotkeys">
            {draft.hotkeys.custom.map((item, index) => (
              <div className="custom-hotkey" key={item.id}>
                <input
                  value={item.label}
                  onChange={(event) => {
                    const custom = [...draft.hotkeys.custom]
                    custom[index] = { ...item, label: event.target.value }
                    setDraft({ ...draft, hotkeys: { ...draft.hotkeys, custom } })
                  }}
                  placeholder="动作名称"
                />
                <input
                  value={item.combo}
                  onChange={(event) => {
                    const custom = [...draft.hotkeys.custom]
                    custom[index] = { ...item, combo: event.target.value }
                    setDraft({ ...draft, hotkeys: { ...draft.hotkeys, custom } })
                  }}
                  placeholder="快捷键"
                />
                <select
                  value={item.action}
                  onChange={(event) => {
                    const custom = [...draft.hotkeys.custom]
                    custom[index] = { ...item, action: event.target.value }
                    setDraft({ ...draft, hotkeys: { ...draft.hotkeys, custom } })
                  }}
                >
                  <option value="showPanel">打开 LocalDeck</option>
                  <option value="showClipboard">打开剪贴板</option>
                  <option value="screenshot">区域截图</option>
                  <option value="screenshotPin">截图并贴图</option>
                  <option value="pinImage">贴图剪贴板图片</option>
                  <option value="proxyToggle">切换代理</option>
                  <option value="openMacro">打开宏编辑器</option>
                  <option value="showLauncher">打开快捷启动</option>
                  <option value="overlayToggle">切换性能悬浮窗</option>
                  <option value="macro">运行宏</option>
                  <option value="launch">打开程序/网址</option>
                  <option value="text">输入文本</option>
                  <option value="lock">锁定电脑</option>
                </select>
                {item.action === 'macro' ? (
                  <select
                    value={item.arguments}
                    onChange={(event) => {
                      const custom = [...draft.hotkeys.custom]
                      custom[index] = { ...item, arguments: event.target.value }
                      setDraft({ ...draft, hotkeys: { ...draft.hotkeys, custom } })
                    }}
                  >
                    <option value="">选择宏</option>
                    {draft.macros.map((macro) => (
                      <option key={macro.id} value={macro.id}>{macro.name}</option>
                    ))}
                  </select>
                ) : (
                  <input
                    value={item.arguments}
                    onChange={(event) => {
                      const custom = [...draft.hotkeys.custom]
                      custom[index] = { ...item, arguments: event.target.value }
                      setDraft({ ...draft, hotkeys: { ...draft.hotkeys, custom } })
                    }}
                    placeholder="参数，可选"
                  />
                )}
                <button
                  className="step-remove"
                  onClick={() =>
                    setDraft({
                      ...draft,
                      hotkeys: {
                        ...draft.hotkeys,
                        custom: draft.hotkeys.custom.filter((entry) => entry.id !== item.id),
                      },
                    })
                  }
                >
                  ×
                </button>
              </div>
            ))}
          </div>
          <button
            className="button"
            onClick={() =>
              setDraft({
                ...draft,
                hotkeys: {
                  ...draft.hotkeys,
                  custom: [
                    ...draft.hotkeys.custom,
                    {
                      id: crypto.randomUUID().replaceAll('-', ''),
                      label: '新动作',
                      combo: '',
                      action: 'showPanel',
                      arguments: '',
                    },
                  ],
                },
              })
            }
          >
            + 添加自定义快捷键
          </button>
        </section>
        <section className="panel hint-panel">
          <strong>与系统冲突时</strong>
          <p>状态会显示“已注册”或具体失败原因。截图默认 `F1`，贴图默认 `F3`；修改后立即重新注册，不需要重启。</p>
        </section>
        <button className="button primary" onClick={() => void onSave(draft)}>保存并重新注册</button>
      </div>
    </div>
  )
}

function DeviceManagerPage({
  devices,
  onRefresh,
  onToggle,
  onProperties,
  onOpenWindows,
  onRescan,
}: {
  devices: DeviceInfo[]
  onRefresh: () => void
  onToggle: (device: DeviceInfo, enabled: boolean) => Promise<void>
  onProperties: (device: DeviceInfo) => Promise<void>
  onOpenWindows: () => Promise<unknown>
  onRescan: () => Promise<void>
}) {
  const [filter, setFilter] = useState('')
  const [classFilter, setClassFilter] = useState('全部')
  const [bluetoothBatteries, setBluetoothBatteries] = useState<BluetoothBatteryInfo[]>([])
  useEffect(() => {
    void invoke<BluetoothBatteryInfo[]>('getBluetoothBattery').then(setBluetoothBatteries)
  }, [])
  const classes = ['全部', ...Array.from(new Set(devices.map((device) => device.class))).sort()]
  const visible = devices.filter((device) =>
    (classFilter === '全部' || device.class === classFilter) &&
    `${device.name} ${device.manufacturer} ${device.instanceId}`.toLowerCase().includes(filter.toLowerCase()),
  )
  return (
    <>
      <section className="panel device-manager-toolbar">
        <div className="search-box">
          <Icon name="search" />
          <input value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="搜索名称、厂商或硬件 ID…" />
        </div>
        <select value={classFilter} onChange={(event) => setClassFilter(event.target.value)}>
          {classes.map((item) => <option key={item}>{item}</option>)}
        </select>
        <button className="button" onClick={() => void onRescan()}>重新扫描</button>
        <button className="button" onClick={() => void onRefresh()}>刷新状态</button>
        <button className="button" onClick={() => void onOpenWindows()}>完整设备管理器</button>
      </section>
      {bluetoothBatteries.length > 0 && (
        <div className="bluetooth-batteries">
          {bluetoothBatteries.map((device) => (
            <span key={device.address}><Icon name="bluetooth" /><strong>{device.name}</strong><b>{device.batteryPercent}%</b></span>
          ))}
        </div>
      )}
      <section className="device-table">
        <div className="device-table-head">
          <span>设备</span><span>类别</span><span>状态</span><span>服务</span><span>操作</span>
        </div>
        {visible.map((device) => {
          const disabled = device.problemCode === 22
          return (
            <article className="device-row-manager" key={device.instanceId}>
              <div>
                <strong>{device.name}</strong>
                <span>{device.manufacturer || '未知厂商'} · {device.instanceId}</span>
              </div>
              <span className="device-class">{device.class}</span>
              <span className={`device-status ${device.problemCode === 0 ? 'ok' : 'bad'}`}>{device.status}</span>
              <span>{device.service || '—'}</span>
              <div className="device-actions">
                <button onClick={() => void onProperties(device)}>属性</button>
                <button
                  disabled={!device.canToggle}
                  onClick={() => {
                    const action = disabled ? '启用' : '禁用'
                    if (window.confirm(`确认要${action}“${device.name}”吗？此操作需要管理员权限。`)) {
                      void onToggle(device, disabled)
                    }
                  }}
                >
                  {disabled ? '启用' : '禁用'}
                </button>
              </div>
            </article>
          )
        })}
        {!visible.length && <EmptyState text="没有匹配的外设" />}
      </section>
      <p className="hint">鼠标、键盘、HID、显示器与电池设备禁止在这里直接禁用，避免锁死输入。其余设备仍可能要求管理员权限。</p>
    </>
  )
}

function TaskCenter({
  startup,
  tasks,
  onRefresh,
  onCleanup,
  onStartupToggle,
  onTaskToggle,
  onTaskRun,
  onTaskStop,
}: {
  startup: StartupItem[]
  tasks: ScheduledTask[]
  onRefresh: () => void
  onCleanup: () => Promise<void>
  onStartupToggle: (item: StartupItem, enabled: boolean) => Promise<void>
  onTaskToggle: (task: ScheduledTask, enabled: boolean) => Promise<void>
  onTaskRun: (task: ScheduledTask) => Promise<void>
  onTaskStop: (task: ScheduledTask) => Promise<void>
}) {
  const [tab, setTab] = useState<'startup' | 'tasks'>('startup')
  const [filter, setFilter] = useState('')
  const visibleTasks = tasks.filter((task) =>
    `${task.path} ${task.name}`.toLowerCase().includes(filter.toLowerCase()),
  )
  return (
    <section className="panel">
      <div className="task-toolbar">
        <div className="segmented task-tabs">
          <button className={tab === 'startup' ? 'active' : ''} onClick={() => setTab('startup')}>开机启动 ({startup.length})</button>
          <button className={tab === 'tasks' ? 'active' : ''} onClick={() => setTab('tasks')}>计划任务 ({tasks.length})</button>
        </div>
        <div className="search-box">
          <Icon name="search" />
          <input value={filter} onChange={(event) => setFilter(event.target.value)} placeholder="筛选任务…" />
        </div>
        <button className="button" onClick={onRefresh}>刷新</button>
        <button className="button" onClick={() => void onCleanup()}>清理失效项</button>
      </div>

      {tab === 'startup' ? (
        <div className="task-list">
          {startup.filter((item) => `${item.name} ${item.command}`.toLowerCase().includes(filter.toLowerCase())).map((item) => (
            <div className="task-row" key={item.id}>
              <div className="task-icon"><Icon name="power" /></div>
              <div className="task-copy">
                <strong>{item.name}</strong>
                <span>{item.scope} · {item.kind} · {item.command || '启动文件夹'}</span>
              </div>
              <span className={`task-state ${item.enabled ? 'ok' : ''}`}>{item.enabled ? '已启用' : '已禁用'}</span>
              <Toggle checked={item.enabled} onChange={(enabled) => void onStartupToggle(item, enabled)} />
            </div>
          ))}
        </div>
      ) : (
        <div className="task-list">
          {visibleTasks.map((task) => (
            <div className="task-row" key={task.path}>
              <div className="task-icon"><Icon name="clock" /></div>
              <div className="task-copy">
                <strong>{task.name}</strong>
                <span>{task.path}</span>
                <em>上次 {formatTime(task.lastRun)} · 下次 {formatTime(task.nextRun)}</em>
              </div>
              <span className={`task-state ${task.state === '运行中' ? 'running' : task.enabled ? 'ok' : ''}`}>{task.state}</span>
              <div className="task-actions">
                <button onClick={() => void onTaskRun(task)}>运行</button>
                <button onClick={() => void onTaskStop(task)}>停止</button>
                <Toggle checked={task.enabled} onChange={(enabled) => void onTaskToggle(task, enabled)} />
              </div>
            </div>
          ))}
          {!visibleTasks.length && <EmptyState text="没有匹配的计划任务" />}
        </div>
      )}
    </section>
  )
}

function MacroPage({
  settings,
  onSave,
}: {
  settings: AppSettings
  onSave: (settings: AppSettings) => Promise<void>
}) {
  const [draft, setDraft] = useState(settings)
  useEffect(() => setDraft(settings), [settings])
  return (
    <div className="stack">
      <section className="panel">
        <PanelHeading
          title="侧键宏"
          action={
            <div className="macro-head-actions">
              <button className="button" onClick={() => void invoke('openMacroWindow')}>
                独立宏窗口
              </button>
              <Toggle
                checked={draft.mouseMacrosEnabled}
                onChange={(value) => setDraft({ ...draft, mouseMacrosEnabled: value })}
              />
            </div>
          }
        />
        <p className="hint">低级鼠标钩子会拦截已绑定的 XButton1/XButton2，并在任意应用中执行宏。键盘、文本与鼠标点击动作通过 Windows SendInput 发送。</p>
        <MacroEditor
          macros={draft.macros}
          bindings={draft.macroBindings}
          onChange={(macros) => setDraft({ ...draft, macros })}
          onBindingsChange={(macroBindings) => setDraft({ ...draft, macroBindings })}
        />
      </section>
      <div className="macro-footer">
        <div className="macro-test-list">
          {draft.macros.map((macro) => (
            <button key={macro.id} className="button" onClick={() => void invoke('runMacro', { id: macro.id })}>
              测试：{macro.name}
            </button>
          ))}
        </div>
        <button className="button primary" onClick={() => void onSave(draft)}>保存宏和侧键绑定</button>
      </div>
    </div>
  )
}

function SettingsPage({
  settings,
  app,
  onSave,
  onOverlayChange,
  onOpenData,
}: {
  settings: AppSettings
  app: Bootstrap['app'] | null
  onSave: (settings: AppSettings, message?: string) => Promise<void>
  onOverlayChange: (patch: {
    enabled?: boolean
    opacity?: number
    fpsSource?: string
    metrics?: string[]
    width?: number
    height?: number
  }) => Promise<AppSettings | null>
  onOpenData: () => void
}) {
  const [draft, setDraft] = useState(settings)
  const [overlayOptions, setOverlayOptions] = useState<OverlayOptions>({
    processes: [],
    displays: [],
  })
  useEffect(() => setDraft(settings), [settings])
  useEffect(() => {
    void invoke<OverlayOptions>('getOverlayOptions').then(setOverlayOptions)
  }, [])
  const updateOverlay = async (patch: {
    enabled?: boolean
    opacity?: number
    fpsSource?: string
    metrics?: string[]
    width?: number
    height?: number
  }) => {
    setDraft((current) => ({
      ...current,
      ...(patch.enabled === undefined ? {} : { performanceOverlayEnabled: patch.enabled }),
      ...(patch.opacity === undefined ? {} : { performanceOverlayOpacity: patch.opacity }),
      ...(patch.fpsSource === undefined ? {} : { performanceOverlayFpsSource: patch.fpsSource }),
      ...(patch.metrics === undefined ? {} : { performanceOverlayMetrics: patch.metrics }),
      ...(patch.width === undefined ? {} : { performanceOverlayWidth: patch.width }),
      ...(patch.height === undefined ? {} : { performanceOverlayHeight: patch.height }),
    }))
    const saved = await onOverlayChange(patch)
    if (saved) {
      setDraft(saved)
    }
  }
  return (
    <div className="two-column settings-layout">
      <div className="stack">
        <section className="panel">
          <PanelHeading title="常规" action={<span className="badge">本机配置</span>} />
          <SettingRow title="关闭时最小化到托盘" description="保留全局热键和剪贴板监听">
            <Toggle checked={draft.minimizeToTray} onChange={(value) => setDraft({ ...draft, minimizeToTray: value })} />
          </SettingRow>
          <SettingRow title="随 Windows 启动" description="使用 HKCU Run，不需要管理员权限">
            <Toggle checked={draft.launchAtLogin} onChange={(value) => setDraft({ ...draft, launchAtLogin: value })} />
          </SettingRow>
          <SettingRow title="记录剪贴板图片" description="大图会转成 PNG 并加密保存">
            <Toggle checked={draft.clipboardCaptureImages} onChange={(value) => setDraft({ ...draft, clipboardCaptureImages: value })} />
          </SettingRow>
          <SettingRow title="显示性能悬浮窗" description="CPU/GPU、温度、帧率和蓝牙设备电量">
            <Toggle
              checked={draft.performanceOverlayEnabled}
              onChange={(value) => void updateOverlay({ enabled: value })}
            />
          </SettingRow>
        </section>

        <section className="panel">
          <PanelHeading title="性能悬浮窗" action={<span className="badge">实时生效</span>} />
          <SettingRow title="透明度" description="只改变背景透明度，指标颜色始终保持清晰">
            <div className="overlay-opacity-control">
              <input
                className="range"
                type="range"
                min={30}
                max={100}
                value={Math.round(draft.performanceOverlayOpacity * 100)}
                onChange={(event) =>
                  void updateOverlay({
                    opacity: Number(event.target.value) / 100,
                  })}
              />
              <b>{Math.round(draft.performanceOverlayOpacity * 100)}%</b>
            </div>
          </SettingRow>
          <SettingRow title="窗口大小" description="也可直接拖动悬浮窗右下角，松开后自动保存">
            <div className="overlay-size-control">
              <label>
                <span>宽</span>
                <input
                  type="number"
                  min={220}
                  max={800}
                  value={Math.round(draft.performanceOverlayWidth)}
                  onChange={(event) =>
                    void updateOverlay({ width: Number(event.target.value) })}
                />
              </label>
              <label>
                <span>高</span>
                <input
                  type="number"
                  min={180}
                  max={900}
                  value={Math.round(draft.performanceOverlayHeight)}
                  onChange={(event) =>
                    void updateOverlay({ height: Number(event.target.value) })}
                />
              </label>
            </div>
          </SettingRow>
          <SettingRow title="FPS 来源" description="统计前台程序、指定进程，或显示指定屏幕刷新率">
            <select
              value={draft.performanceOverlayFpsSource}
              onChange={(event) =>
                void updateOverlay({ fpsSource: event.target.value })}
            >
              <option value="foreground">当前前台程序</option>
              <optgroup label="指定屏幕">
                {overlayOptions.displays.map((display) => (
                  <option key={display.id} value={`screen:${display.deviceName ?? ''}`}>
                    {display.name} · {display.deviceName}
                  </option>
                ))}
              </optgroup>
              <optgroup label="指定进程">
                {overlayOptions.processes.map((name) => (
                  <option key={name} value={`process:${name}`}>{name}</option>
                ))}
              </optgroup>
            </select>
          </SettingRow>
          <div className="overlay-metrics-config">
            <strong>显示指标</strong>
            <p>可添加 LocalDeck 当前能稳定获取的全部指标。</p>
            <div className="overlay-metric-options">
              {overlayMetricOptions.map(([key, label]) => {
                const checked = draft.performanceOverlayMetrics.includes(key)
                return (
                  <label className={`overlay-metric-option ${checked ? 'active' : ''}`} key={key}>
                    <input
                      type="checkbox"
                      checked={checked}
                      onChange={() =>
                        void updateOverlay({
                          metrics: checked
                            ? draft.performanceOverlayMetrics.filter((item) => item !== key)
                            : [...draft.performanceOverlayMetrics, key],
                        })}
                    />
                    <span>{label}</span>
                  </label>
                )
              })}
            </div>
          </div>
        </section>

        <section className="panel">
          <PanelHeading title="全局热键" action={<span className="small-note">前往快捷键中心编辑</span>} />
          <div className="shortcut-list">
            <Shortcut keys={draft.hotkeys.clipboard} label="打开剪贴板" />
            <Shortcut keys={draft.hotkeys.screenshot} label="区域截图" />
            <Shortcut keys={draft.hotkeys.pinImage} label="贴图剪贴板图片" />
            <Shortcut keys={draft.hotkeys.search} label="文件搜索" />
          </div>
        </section>

        <section className="panel">
          <PanelHeading title="流量转发接口" action={<span className="badge">无外部核心</span>} />
          <label className="path-field">
            <span>当前 YAML 配置</span>
            <input
              value={draft.proxyProfilePath ?? ''}
              readOnly
            />
          </label>
          <label className="path-field">
            <span>混合代理端口</span>
            <input
              type="number"
              min={1024}
              max={65535}
              value={draft.proxyPort}
              onChange={(event) => setDraft({ ...draft, proxyPort: Number(event.target.value) })}
            />
          </label>
          <SettingRow title="随 LocalDeck 启动流量转发" description="默认开启本地 127.0.0.1 混合代理，不修改系统代理">
            <Toggle checked={draft.proxyAutoStart} onChange={(value) => setDraft({ ...draft, proxyAutoStart: value })} />
          </SettingRow>
        </section>

        <section className="panel">
          <PanelHeading title="本地模型引擎" action={<span className="badge">按需加载</span>} />
          <label className="path-field">
            <span>GGUF 模型目录</span>
            <input
              value={draft.localModelDirectory ?? ''}
              onChange={(event) => setDraft({ ...draft, localModelDirectory: event.target.value || null })}
            />
          </label>
          <label className="path-field">
            <span>llama.cpp 引擎目录</span>
            <input
              value={draft.localModelEngineDirectory ?? ''}
              onChange={(event) => setDraft({ ...draft, localModelEngineDirectory: event.target.value || null })}
            />
          </label>
          <label className="path-field">
            <span>模型服务端口</span>
            <input
              type="number"
              min={1024}
              max={65535}
              value={draft.localModelPort}
              onChange={(event) => setDraft({ ...draft, localModelPort: Number(event.target.value) })}
            />
          </label>
        </section>
      </div>

      <div className="stack">
        <section className="panel privacy-panel">
          <div className="shield"><Icon name="shield" /></div>
          <h2>隐私边界</h2>
          <p>LocalDeck 不包含注册、登录、广告、自动更新、云同步、远程控制或使用统计。配置、剪贴板和文件索引均留在本机。</p>
          <ul>
            <li>内置代理的节点通信需要联网，这是功能必需。</li>
            <li>厂商风扇/温度接口来自已安装的 Pegatron 驱动。</li>
            <li>Logitech 设备通过 HID++ 直接访问，不经 LGHUB 账号。</li>
          </ul>
        </section>
        <section className="panel">
          <PanelHeading title="关于" />
          <div className="about-grid">
            <span>版本</span><b>{app?.version ?? '0.1.0'}</b>
            <span>设备</span><b>{app?.machine ?? '—'}</b>
            <span>系统</span><b>{app?.os ?? '—'}</b>
            <span>界面</span><b>WebView2 · React</b>
          </div>
          <button className="button wide" onClick={onOpenData}><Icon name="folder" /> 打开本地数据目录</button>
        </section>
        <button className="button primary save-button" onClick={() => void onSave(draft)}>保存设置</button>
      </div>
    </div>
  )
}

function Metric({
  icon,
  label,
  value,
  detail,
  progress,
  tone,
}: {
  icon: IconName
  label: string
  value: string
  detail: string
  progress: number
  tone: string
}) {
  return (
    <article className={`metric-card tone-${tone}`}>
      <div className="metric-head"><span><Icon name={icon} /></span><em>{label}</em></div>
      <strong>{value}</strong>
      <p>{detail}</p>
      <div className="progress-track"><div style={{ width: `${Math.min(100, Math.max(0, progress))}%` }} /></div>
    </article>
  )
}

function ModeButton({
  title,
  subtitle,
  icon,
  tone,
  onClick,
}: {
  title: string
  subtitle: string
  icon: IconName
  tone?: string
  onClick: () => void
}) {
  return (
    <button className={`mode-button ${tone ?? ''}`} onClick={onClick}>
      <span><Icon name={icon} /></span>
      <strong>{title}</strong>
      <em>{subtitle}</em>
    </button>
  )
}

function ProcessList({ processes, expanded = false }: { processes: ProcessInfo[]; expanded?: boolean }) {
  return (
    <div className={`process-list ${expanded ? 'expanded' : ''}`}>
      {processes.map((process, index) => (
        <div key={process.id}>
          <span className="process-rank">{String(index + 1).padStart(2, '0')}</span>
          <strong>{process.name}</strong>
          <span>PID {process.id}</span>
          <b>{formatBytes(process.workingSetBytes)}</b>
          <div className="process-bar"><span style={{ width: `${Math.min(100, (process.workingSetBytes / Math.max(1, processes[0]?.workingSetBytes ?? 1)) * 100)}%` }} /></div>
        </div>
      ))}
      {processes.length === 0 && <EmptyState text="暂无进程采样" />}
    </div>
  )
}

function StatusLine({
  icon,
  title,
  value,
  detail,
  ok,
}: {
  icon: IconName
  title: string
  value: string
  detail: string
  ok: boolean
}) {
  return (
    <div className="status-line">
      <div className="status-icon"><Icon name={icon} /></div>
      <div><span>{title}</span><strong>{value}</strong><em>{detail}</em></div>
      <div className={`status-dot ${ok ? 'ok' : ''}`} />
    </div>
  )
}

function Sensor({ label, value }: { label: string; value: string }) {
  return <div className="sensor"><span>{label}</span><strong>{value}</strong></div>
}

function SettingRow({ title, description, children }: { title: string; description: string; children: React.ReactNode }) {
  return (
    <div className="setting-row">
      <div><strong>{title}</strong><span>{description}</span></div>
      {children}
    </div>
  )
}

function Shortcut({ keys, label }: { keys: string; label: string }) {
  return <div className="shortcut"><span>{label}</span><kbd>{keys}</kbd></div>
}

function Toggle({ checked, onChange }: { checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <button className={`toggle ${checked ? 'checked' : ''}`} onClick={() => onChange(!checked)} aria-pressed={checked}>
      <span />
    </button>
  )
}

function PanelHeading({ title, action }: { title: string; action?: React.ReactNode }) {
  return <div className="panel-heading"><h2>{title}</h2>{action}</div>
}

function EmptyState({ text }: { text: string }) {
  return <div className="empty-state"><Icon name="info" /><span>{text}</span></div>
}

type IconName =
  | 'dashboard' | 'monitor' | 'gauge' | 'network' | 'clipboard' | 'search' | 'volume'
  | 'settings' | 'camera' | 'cpu' | 'memory' | 'fan' | 'battery' | 'sun' | 'download'
  | 'upload' | 'leaf' | 'gamepad' | 'bolt' | 'link' | 'pin' | 'copy' | 'trash'
  | 'close' | 'folder' | 'open' | 'mute' | 'shield' | 'info' | 'image' | 'file'
  | 'code' | 'archive' | 'keyboard' | 'plug' | 'clock' | 'mouse' | 'power'
  | 'grid' | 'play' | 'rocket' | 'pie' | 'driver' | 'brain' | 'log'
  | 'bluetooth'

function Icon({ name }: { name: IconName }) {
  const paths: Record<IconName, React.ReactNode> = {
    dashboard: <><rect x="3" y="3" width="7" height="7" rx="2" /><rect x="14" y="3" width="7" height="7" rx="2" /><rect x="3" y="14" width="7" height="7" rx="2" /><rect x="14" y="14" width="7" height="7" rx="2" /></>,
    monitor: <><rect x="2" y="4" width="20" height="13" rx="2" /><path d="M8 21h8M12 17v4" /></>,
    gauge: <><path d="M4.9 19a9 9 0 1 1 14.2 0" /><path d="m12 13 4-4" /><circle cx="12" cy="13" r="1.5" /></>,
    network: <><rect x="9" y="2" width="6" height="6" rx="1.5" /><rect x="2" y="16" width="6" height="6" rx="1.5" /><rect x="16" y="16" width="6" height="6" rx="1.5" /><path d="M12 8v4M5 16v-2h14v2" /></>,
    clipboard: <><rect x="5" y="4" width="14" height="17" rx="2" /><path d="M9 4V2h6v2M8 10h8M8 14h8M8 18h5" /></>,
    search: <><circle cx="11" cy="11" r="7" /><path d="m20 20-4-4" /></>,
    volume: <><path d="M4 10v4h4l5 4V6L8 10H4Z" /><path d="M16 9a4 4 0 0 1 0 6M18.5 6.5a8 8 0 0 1 0 11" /></>,
    settings: <><circle cx="12" cy="12" r="3" /><path d="M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1-2.8 2.8-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.6v.2h-4V21a1.7 1.7 0 0 0-1-1.6 1.7 1.7 0 0 0-1.9.3l-.1.1L4.2 17l.1-.1a1.7 1.7 0 0 0 .3-1.9A1.7 1.7 0 0 0 3 14H2.8v-4H3a1.7 1.7 0 0 0 1.6-1 1.7 1.7 0 0 0-.3-1.9L4.2 7 7 4.2l.1.1A1.7 1.7 0 0 0 9 4.6 1.7 1.7 0 0 0 10 3V2.8h4V3a1.7 1.7 0 0 0 1 1.6 1.7 1.7 0 0 0 1.9-.3l.1-.1L19.8 7l-.1.1a1.7 1.7 0 0 0-.3 1.9 1.7 1.7 0 0 0 1.6 1h.2v4H21a1.7 1.7 0 0 0-1.6 1Z" /></>,
    camera: <><path d="M4 7h3l1.5-2h7L17 7h3v12H4V7Z" /><circle cx="12" cy="13" r="4" /></>,
    cpu: <><rect x="6" y="6" width="12" height="12" rx="2" /><rect x="9" y="9" width="6" height="6" rx="1" /><path d="M9 2v4M15 2v4M9 18v4M15 18v4M2 9h4M2 15h4M18 9h4M18 15h4" /></>,
    memory: <><rect x="3" y="7" width="18" height="10" rx="2" /><path d="M7 10v4M11 10v4M15 10v4M19 10v4M7 17v3M17 17v3" /></>,
    fan: <><circle cx="12" cy="12" r="2" /><path d="M12 10c-2-5-1-8 2-7 3 1 1 5-2 7ZM14 12c5-2 8-1 7 2-1 3-5 1-7-2ZM12 14c2 5 1 8-2 7-3-1-1-5 2-7ZM10 12c-5 2-8 1-7-2 1-3 5-1 7 2Z" /></>,
    battery: <><rect x="3" y="7" width="17" height="10" rx="2" /><path d="M20 10v4M6 10h6v4H6z" /></>,
    sun: <><circle cx="12" cy="12" r="4" /><path d="M12 2v3M12 19v3M4.9 4.9 7 7M17 17l2.1 2.1M2 12h3M19 12h3M4.9 19.1 7 17M17 7l2.1-2.1" /></>,
    download: <><path d="M12 3v12M7 10l5 5 5-5M5 21h14" /></>,
    upload: <><path d="M12 21V9M7 14l5-5 5 5M5 3h14" /></>,
    leaf: <><path d="M20 4C10 4 4 8 4 15c0 3 2 5 5 5 7 0 11-6 11-16Z" /><path d="M8 20c1-6 5-9 9-12" /></>,
    gamepad: <><path d="M8 8h8a5 5 0 0 1 4.8 6.4l-1 3.2a2.5 2.5 0 0 1-4.2 1L14 16h-4l-1.6 2.6a2.5 2.5 0 0 1-4.2-1l-1-3.2A5 5 0 0 1 8 8Z" /><path d="M7 11v4M5 13h4M16 12h.01M18 14h.01" /></>,
    bolt: <><path d="m13 2-8 12h6l-1 8 8-12h-6l1-8Z" /></>,
    link: <><path d="M10 13a5 5 0 0 0 7.5.5l2-2a5 5 0 0 0-7-7l-1.5 1.5M14 11a5 5 0 0 0-7.5-.5l-2 2a5 5 0 0 0 7 7l1.5-1.5" /></>,
    pin: <><path d="M9 3h6l-1 6 4 4v2H6v-2l4-4-1-6ZM12 15v6" /></>,
    copy: <><rect x="8" y="8" width="12" height="12" rx="2" /><path d="M16 8V6a2 2 0 0 0-2-2H6a2 2 0 0 0-2 2v8a2 2 0 0 0 2 2h2" /></>,
    trash: <><path d="M4 7h16M9 7V4h6v3M7 7l1 14h8l1-14M10 11v6M14 11v6" /></>,
    close: <><path d="m6 6 12 12M18 6 6 18" /></>,
    folder: <><path d="M3 6h7l2 2h9v11H3V6Z" /></>,
    open: <><path d="M14 4h6v6M20 4l-9 9M18 13v7H4V6h7" /></>,
    mute: <><path d="M4 10v4h4l5 4V6L8 10H4Z" /><path d="m16 10 4 4M20 10l-4 4" /></>,
    shield: <><path d="M12 3 20 6v5c0 5-3 8-8 10-5-2-8-5-8-10V6l8-3Z" /><path d="m9 12 2 2 4-5" /></>,
    info: <><circle cx="12" cy="12" r="9" /><path d="M12 11v6M12 7h.01" /></>,
    image: <><rect x="3" y="4" width="18" height="16" rx="2" /><circle cx="9" cy="9" r="2" /><path d="m4 17 5-5 4 4 2-2 5 5" /></>,
    file: <><path d="M6 2h8l4 4v16H6V2Z" /><path d="M14 2v5h5" /></>,
    code: <><path d="m8 9-4 3 4 3M16 9l4 3-4 3M14 5l-4 14" /></>,
    archive: <><rect x="4" y="4" width="16" height="16" rx="2" /><path d="M9 4v4h6V4M10 12h4M10 16h4" /></>,
    keyboard: <><rect x="2" y="6" width="20" height="12" rx="2" /><path d="M5 9h.01M8 9h.01M11 9h.01M14 9h.01M17 9h.01M19 9h.01M5 12h.01M8 12h.01M11 12h.01M14 12h.01M17 12h.01M19 12h.01M7 15h10" /></>,
    plug: <><path d="M8 3v5M16 3v5M6 8h12v2a6 6 0 0 1-6 6v0a6 6 0 0 1-6-6V8ZM12 16v5" /></>,
    clock: <><circle cx="12" cy="12" r="9" /><path d="M12 7v5l3 2" /></>,
    mouse: <><rect x="6" y="2" width="12" height="20" rx="6" /><path d="M12 2v7M6 9h12M17 5l3-2M19 7l3-1" /></>,
    power: <><path d="M12 2v10M6.3 5.3a9 9 0 1 0 11.4 0" /></>,
    grid: <><rect x="3" y="3" width="7" height="7" rx="1" /><rect x="14" y="3" width="7" height="7" rx="1" /><rect x="3" y="14" width="7" height="7" rx="1" /><rect x="14" y="14" width="7" height="7" rx="1" /></>,
    play: <><polygon points="8,5 19,12 8,19" /></>,
    rocket: <><path d="M5 15c3-6 7-10 14-11-1 7-5 11-11 14l-3-3Z" /><path d="m8 16 4-4M6 18l-2 2M15 5l4 4" /></>,
    pie: <><path d="M12 3v9h9a9 9 0 1 1-9-9Z" /><path d="M12 3a9 9 0 0 1 9 9h-9V3Z" /></>,
    driver: <><rect x="4" y="4" width="16" height="16" rx="3" /><path d="M8 8h8v4H8zM8 16h8M7 3v2M12 3v2M17 3v2" /></>,
    brain: <><path d="M9 4a3 3 0 0 0-3 3v1a3 3 0 0 0-1 5 3 3 0 0 0 2 4 3 3 0 0 0 5 1V5a3 3 0 0 0-3-1ZM15 4a3 3 0 0 1 3 3v1a3 3 0 0 1 1 5 3 3 0 0 1-2 4 3 3 0 0 1-5 1" /></>,
    log: <><path d="M4 5h16v14H4V5Z" /><path d="M8 9h8M8 13h8M8 17h5" /></>,
    bluetooth: <><path d="m7 7 10 10-5 4V3l5 4L7 17" /></>,
  }
  return (
    <svg className="icon" viewBox="0 0 24 24" aria-hidden="true">
      {paths[name]}
    </svg>
  )
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const power = Math.min(units.length - 1, Math.floor(Math.log(bytes) / Math.log(1024)))
  return `${(bytes / 1024 ** power).toFixed(power === 0 ? 0 : 1)} ${units[power]}`
}

function formatRate(bytes: number): string {
  return `${formatBytes(bytes)}/s`
}

function formatTime(value: string): string {
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return value
  return new Intl.DateTimeFormat('zh-CN', {
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date)
}

function modeLabel(mode: string): string {
  return ({ rule: '规则', global: '全局', direct: '直连', office: '办公', gaming: '游戏', turbo: '狂飙' } as Record<string, string>)[mode] ?? mode
}

function clipIcon(kind: string): IconName {
  if (kind === 'image') return 'image'
  if (kind === 'html' || kind === 'rtf') return 'code'
  return 'file'
}

function fileIcon(name: string): IconName {
  const extension = name.split('.').pop()?.toLowerCase()
  if (['png', 'jpg', 'jpeg', 'gif', 'webp', 'bmp', 'svg'].includes(extension ?? '')) return 'image'
  if (['zip', '7z', 'rar', 'tar', 'gz', 'xz'].includes(extension ?? '')) return 'archive'
  if (['js', 'ts', 'tsx', 'jsx', 'cs', 'py', 'rs', 'go', 'json', 'yaml', 'yml', 'md', 'html', 'css'].includes(extension ?? '')) return 'code'
  return 'file'
}

function highlight(text: string, query: string): React.ReactNode {
  const trimmed = query.trim()
  if (!trimmed) return text
  const index = text.toLowerCase().indexOf(trimmed.toLowerCase())
  if (index < 0) return text
  return <>{text.slice(0, index)}<mark>{text.slice(index, index + trimmed.length)}</mark>{text.slice(index + trimmed.length)}</>
}

function App() {
  const isMacroWindow = new URLSearchParams(window.location.search).get('view') === 'macro'
  return isMacroWindow ? <MacroWindowApp /> : <MainApplication />
}

function MacroWindowApp() {
  const [settings, setSettings] = useState<AppSettings | null>(null)
  const [recording, setRecording] = useState(false)
  const [recordingSteps, setRecordingSteps] = useState<MacroStep[]>([])
  const [status, setStatus] = useState('')
  const [error, setError] = useState('')

  useEffect(() => {
    invoke<AppSettings>('getSettings')
      .then(setSettings)
      .catch((cause) => setError(messageOf(cause)))
  }, [])

  function addRecordedMacro(steps: MacroStep[]) {
    if (!steps.length) {
      setStatus('录制结束：没有捕获到输入')
      return
    }
    const id = crypto.randomUUID().replaceAll('-', '')
    setSettings((current) => current ? {
      ...current,
      macros: [
        ...current.macros,
        {
          id,
          name: `录制 ${new Date().toLocaleTimeString('zh-CN', { hour: '2-digit', minute: '2-digit' })}`,
          enabled: true,
          steps,
        },
      ],
    } : current)
    setStatus(`录制完成：${steps.length} 个步骤。按 F8 停止录制。`)
  }

  useEffect(() => {
    if (!recording) return
    const timer = window.setInterval(async () => {
      try {
        const result = await invoke<{ recording: boolean; steps: MacroStep[] }>('getMacroRecording')
        setRecordingSteps(result.steps)
        if (!result.recording) {
          setRecording(false)
          addRecordedMacro(result.steps)
        }
      } catch {
        // Polling can race with F8 stopping the hooks.
      }
    }, 400)
    return () => window.clearInterval(timer)
  }, [recording])

  const startRecording = async () => {
    setError('')
    setRecordingSteps([])
    const result = await invoke<{ ok: boolean }>('startMacroRecording')
    if (!result.ok) {
      setError('无法安装键盘/鼠标录制钩子')
      return
    }
    setRecording(true)
    setStatus('录制中：正常操作键盘、鼠标或侧键，按 F8 停止。')
  }

  const stopRecording = async () => {
    const result = await invoke<{ steps: MacroStep[] }>('stopMacroRecording')
    setRecording(false)
    addRecordedMacro(result.steps)
  }

  if (!settings) {
    return (
      <div className="macro-window-shell">
        <div className="macro-window-loading">{error || '正在加载宏编辑器…'}</div>
      </div>
    )
  }

  return (
    <div className="macro-window-shell">
      <header className="macro-window-header">
        <div>
          <p className="eyebrow">LOCALDECK MACRO STUDIO</p>
          <h1>Logitech 侧键与宏</h1>
          <p>录制、编辑并测试全局宏，无需 LGHUB。</p>
        </div>
        <div className="macro-record-actions">
          {recording && <span className="recording-pill"><i /> REC</span>}
          <button className="button" onClick={() => void startRecording()} disabled={recording}>开始录制</button>
          <button className="button danger-quiet" onClick={() => void stopRecording()} disabled={!recording}>停止 (F8)</button>
          <button className="button" onClick={() => window.close()}>关闭窗口</button>
        </div>
      </header>
      <main className="macro-window-content">
        <section className="panel">
          <div className="recording-status">
            <div>
              <strong>录制状态</strong>
              <span>{status || '按“开始录制”后，关闭窗口前可切换到任意应用操作。'}</span>
            </div>
            <b>{recordingSteps.length} 步</b>
          </div>
          <MacroEditor
            macros={settings.macros}
            bindings={settings.macroBindings}
            onChange={(macros) => setSettings({ ...settings, macros })}
            onBindingsChange={(macroBindings) => setSettings({ ...settings, macroBindings })}
          />
        </section>
        {error && <p className="error-box">{error}</p>}
        <footer className="macro-window-footer">
          <span>快捷键、键盘组合、文本、点击、坐标点击、程序和延时均可编辑。</span>
          <button className="button primary" onClick={async () => {
            try {
              const saved = await invoke<AppSettings>('saveSettings', { settings })
              setSettings(saved)
              setStatus('宏已保存并立即生效。')
            } catch (cause) {
              setError(messageOf(cause))
            }
          }}>保存全部宏</button>
        </footer>
      </main>
    </div>
  )
}

export default App
