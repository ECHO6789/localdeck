# REA 与外联行为审计

审计目标不是删除所有网络能力，而是区分：

1. **功能必需**：不做就无法完成用户明确动作，例如代理转发、用户指定的模型下载。
2. **本地替代**：可以用 Windows、硬件 API 或本机文件完成，不需要互联网。
3. **删除外联**：自动更新、广告、账号、推荐、遥测、云同步、远控和在线配置。

## 汇总

| 原工具/能力 | 观察到的外联类别 | 判断 | LocalDeck 处理 |
| --- | --- | --- | --- |
| Twinkle Tray | Electron 自动更新指向 GitHub | 亮度不需要联网 | 不打包更新器；WMI + DDC/CI 直接控制 |
| Clash Verge / Mihomo | 节点连接、订阅下载、规则控制器 | 节点转发是功能必需，Clash 进程不是 | 混合代理 + 随包 HY2 引擎；只读取本地 YAML；不启动、不查询 Clash |
| Logitech G HUB | 账号/SSO、更新、推荐、遥测、设备服务 | 账号和推广不需要 | G HUB 已卸载；LocalDeck 使用原生 HID++ 直连 DPI/电量和宏 |
| Snipaste | 截图、贴图、剪贴板、可能的版本检查 | 截图功能不需要联网 | 本地截图、贴图和剪贴板替代 |
| Everything | 本地文件名索引、可选 HTTP/ETP 服务 | 本地搜索必需，服务不需要 | SQLite FTS5；不监听搜索 HTTP/ETP |
| Machenike 智控平台 | 游戏推广、账号验证、用户数据上传、远程配置 | 广告/远程/账号均不需要 | 不启动主程序；仅调用已安装的 PGFNEX/PHotkey 本地接口 |
| LocModel | 模型下载/引擎获取、可能的更新与账号 | 用户主动下载模型是功能动作；更新、遥测不是 | 扫描本机设置但不调用原 UI；模型下载只由用户输入 URL；引擎可复制为自有 |
| 云端模型入口 | ChatGPT/DeepSeek/Grok/Claude/Gemini 网站 | 用户点击即打开网站是功能动作 | 只启动 Chrome 新窗口，不注入令牌、不读 Cookie、不代理登录 |
| Windows Update 驱动 | 驱动目录查询 | 用户点击“检查更新”是功能动作 | 只在显式点击时调用 Windows Update COM |
| DLSS Unlocked | 游戏运行库和补丁下载 | 用户点击下载是功能动作 | 固定版本、校验官方 SHA-256，不自动更新 |
| Geek Uninstaller | 本地卸载登记、残留扫描 | 全部可离线 | 直接读注册表、备份 `.reg`、隔离目录 |
| WizTree | 本地磁盘扫描 | 全部可离线 | 普通文件枚举与占用图；不联网 |
| HWiNFO | 本地传感器、报告/更新 | 传感器可离线，更新不是必需 | 当前机型使用 PGFNEX 常用传感器 |

## REA 结果

对 `LocModel.exe` 使用：

```text
rea info
rea decompile
rea LocModel.exe
```

恢复了约 48,967 个函数，确认了 Process/Network/Cryptography/Registry/File
system 等导入能力和 `locmodel.pdb`。约 904 KB 高熵区域未解密，不能从静态
结果中确认内部下载 URL，因此 LocalDeck 不复制未知下载协议，也不调用
LocModel 的登录、更新或云服务。

可确认的本地配置为：

- 模型目录：`<configured-models-directory>`
- 端口：`8686`
- 引擎：`%LOCALAPPDATA%\LocModel\engines\<engine-version>`

报告位于：

- `%LOCALAPPDATA%\LocModel\re-LocModel.md`
- `%LOCALAPPDATA%\LocModel\re-LocModel.pdf`

## Machenike 具体证据

静态检查 `%ProgramFiles%\Machenike\Machenike.exe` 时发现：

- `valid.gamepp.com/vipinfo`：账号/VIP 验证。
- `valid.gamepp.com/serverinfo?m=upload_client_info`：客户端信息上传。
- `config.gamepp.com/api/v1/uploadUserData`、`downloadUserData`：配置云同步。
- `hwhd.gamepp.com/hotgame/list`、`gamepp.com/game.html`：游戏推广。
- `www.machenike.com/api.php/offline`：厂商在线接口。

这些接口均未进入 LocalDeck。风扇、温度和功率仍使用厂商安装的本地
`PGFNEX64.dll` COM 类和 `PHotkey` 窗口消息；它们不由 LocalDeck 分发。

## LGHUB 和蓝牙

LocalDeck 不启动 LGHUB 的设备服务、账号、更新或推荐进程。优先直接向接收器
发送 HID++ 2.0 长报文。参考 Logitech 接收器已通过 `0x2201` 直接读写 DPI，
并通过 `0x1001` 电压曲线读取电量；G HUB 已从参考电脑卸载。

蓝牙电量只读取已连接 BLE 设备的标准 GATT Battery Service
`0000180f-0000-1000-8000-00805f9b34fb` 和 Battery Level
`00002a19-0000-1000-8000-00805f9b34fb`。经典蓝牙没有通用标准电量特征，
因此不声称支持所有蓝牙设备。

## LocalDeck 自身网络边界

- 无遥测 SDK、自动更新器、登录页、广告、在线字体或在线图标。
- WebView2 前端通过虚拟主机映射本地目录，`DenyCors`，不监听 HTTP 端口。
- 代理只监听 `127.0.0.1`；出站连接只由用户选择的代理节点触发。
- Hysteria2 引擎由 LocalDeck 随包分发，使用 `--disable-update-check`，只监听
  临时本机 SOCKS5 端口，代理停止时同步退出。
- 模型下载只接受用户输入的 HTTP(S) URL 和合法 `.gguf` 文件名。
- DLSS 独立包只在用户点击下载后请求固定 GitHub Release，并校验 SHA-256。
- 云端模型按钮只启动浏览器；第三方网页自身的网络和隐私行为由用户与该网站决定。
- 驱动更新只在点击按钮后查询 Windows Update。
- 文件打开、资源管理器定位、卸载登记和日志分析均在本机执行。
- 用户代理 YAML、VPS 地址、订阅、密码和生成的 HY2 配置只存在本机数据目录，
  不进入 Git、文档示例、截图或发布包。

代理协议和规则边界见 [PROXY.md](PROXY.md)，快捷键和宏见
[SHORTCUTS.md](SHORTCUTS.md)。
