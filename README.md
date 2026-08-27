# Muhun MCSV Manager

Muhun MCSV Manager 是為 Windows 10／11 x64 設計的自架 Minecraft 多伺服器管理工具。目前公開的來源快照版本為 **1.0.6**。

產品採用「Windows Service 唯一寫入者」架構：Minecraft 程序、Port、控制台、備份、模組包更新、遠端帳號、權限、通知、Provider 與產品更新都由背景 Service 統一管理；Windows GUI、Web／PWA 與 Android 客戶端只透過受授權的版本化介面操作。

> **發行狀態：** 本 repository 目前提供原始碼與技術文件。若 [GitHub Releases](https://github.com/llngh39690619-maker/Muhun-MCSV-Manager/releases) 尚未出現完整的 1.0.6 發行資產，表示公開安裝包尚未發布；GitHub 自動產生的 Source code ZIP 不是 Windows 安裝包。

## English summary

Muhun MCSV Manager is a self-hosted Windows desktop and web-based Minecraft server manager. It combines a least-privilege Windows Service, WPF desktop GUI, responsive Web/PWA panel, role-based access control, backups, modpack workflows, notifications, provider isolation, and secure HTTPS remote administration.

The CurseForge integration is designed to use the official API, respect each author's third-party distribution setting, attribute projects and authors, avoid rehosting files, and limit requests through caching and bounded queries. No CurseForge API key or other production credential is committed to this repository.

## 主要功能

- 建立、匯入、啟動、停止、重新啟動及批次管理多個 Minecraft Server。
- Windows Service 持續持有 Server；關閉 GUI 不會終止 Service 管理中的 Minecraft 程序或已啟用的 Web 服務。
- 深色 WPF GUI，包含控制台、錯誤／警告分流、玩家資訊、備份、Java、模組／插件、外觀與伺服器設定。
- 啟動時從 `25565` 起選擇最低可用 TCP Port，並以保留機制避免同時啟動時發生競爭；目前支援 `server.properties` 類型核心與 Velocity，BungeeCord／Waterfall 在安全 YAML 編輯支援完成前會明確拒絕啟動。
- Modrinth、FTB 與 CurseForge 模組包目錄，支援搜尋、排序、遊戲版本、Loader、分類、預覽圖及背景安裝。
- 模組包疊代更新保留世界與玩家資料，先建立回復點，失敗或健康檢查未通過時可回復。
- Eclipse Adoptium Temurin Java 8／11／16／17／21／25 下載與完整性驗證。
- 多帳號、角色、全域與逐 Server 權限、記住裝置、最後 Owner 防護及 SQLite 稽核。
- Responsive Web／PWA；iOS 可加入主畫面，Android 提供只允許 HTTPS 固定來源的 WebView shell。
- SQLite 通知 outbox、重試與去重，以及 Discord Webhook 通知。
- 簽署 `.mcsvp` Provider 套件、隔離 Provider Host、能力限制與網路 host allowlist。
- 繁體中文／英文、深色／黑金主題、字體與視窗預覽，以及每個 Server 的背景與圖示。

## 執行架構

```text
Windows WPF GUI
       │ ACL 保護的 Named Pipe IPC API 1.5
       ▼
MuhunMCSV Windows Service
       ├─ Server Runtime／Port／Console
       ├─ Backup／Modpack Update／Rollback
       ├─ Account／RBAC／Audit
       ├─ Notification／Discord
       ├─ Provider Host
       └─ Loopback Web Host：127.0.0.1:39050
                         ▲
                         │ 已核准的 HTTPS Tunnel
                         │
            Browser／iOS PWA／Android shell
```

Service 名稱為 `MuhunMCSV`，使用最小權限虛擬服務帳號 `NT SERVICE\MuhunMCSV`，不是 LocalSystem。Service 離線或 IPC 版本不相容時，GUI 會 fail closed，不會改成直接控制 Java 或繞過權限。

Web Host 只監聽 loopback，不開放 `0.0.0.0`、UPnP 或路由器 Port Forward。公開遠端存取必須經過已核准的 HTTPS Tunnel。

## 專案結構

```text
src/
├─ MinecraftServerManager.App/              Windows WPF GUI
├─ MinecraftServerManager.Service/          Windows Service
├─ MinecraftServerManager.Remote/           Web／PWA 與遠端 API
├─ MinecraftServerManager.Core/             Server、Java、備份與更新核心
├─ MinecraftServerManager.Data/             SQLite 與資料持久化
├─ MinecraftServerManager.Notifications/    通知與 Discord
├─ MinecraftServerManager.BuiltinProvider/  內建來源 Provider
├─ MinecraftServerManager.ProviderHost/     隔離 Provider Host
├─ MinecraftServerManager.Client/           GUI IPC Client
├─ MinecraftServerManager.Contracts/        版本化契約
└─ MinecraftServerManager.Updater/          A/B 更新協調器

tests/                                      正式測試專案
android/MuhunMcsvRemote/                    Android HTTPS shell
scripts/                                    建置、簽章、驗證與安裝腳本
docs/                                       架構、操作、安全與驗收文件
```

## 系統需求

### 從來源建置

- Windows 10 或 Windows 11 x64。
- .NET SDK `10.0.400`（由 `global.json` 固定）。
- PowerShell 7.4 或更新版本。
- Android 建置另需由專案腳本固定的 JDK、Gradle 與 Android Build Tools。

### 使用正式發行包

- 安裝／升級 Windows Service 時需要系統管理員權限。
- 正式 Windows 執行檔為 self-contained，不需另行安裝 .NET Runtime。
- Minecraft Server 仍須使用符合其版本與 Loader 要求的 Java。

## 從來源建置與測試

Repository 使用 NuGet lock file。一般來源建置是未簽署的開發成品，不等同於正式發行包。

```powershell
dotnet restore .\MinecraftServerManager.sln --locked-mode

dotnet build .\MinecraftServerManager.sln `
  -c Release `
  --no-restore `
  -p:TreatWarningsAsErrors=true

dotnet test .\MinecraftServerManager.sln `
  -c Release `
  --no-build `
  --no-restore `
  -p:TreatWarningsAsErrors=true
```

正式可散發成品必須再經過 self-contained publish、Windows／Provider／APK 簽章、RSA-PSS manifest、逐檔 SHA-256、封裝及獨立磁碟驗證。詳細流程見[正式簽章與安全發布](docs/正式產品-簽章與安全發布.md)。

## Web 與手機管理

1. 在桌面 GUI 建立遠端帳號。
2. 為每個帳號設定全域及逐 Server 權限。
3. 設定 Tailscale Funnel，或使用 Cloudflare Named／Quick Tunnel 相容模式。
4. 從 HTTPS 網址登入 Web 面板。
5. iOS 可使用 Safari「加入主畫面」；Android 可側載正式發行包中的簽署 APK。

遠端後端會重新檢查登入狀態、角色、Server scope、Origin、CSRF 與 Idempotency-Key；前端隱藏按鈕不被視為安全授權。

## CurseForge 與第三方內容

- CurseForge 查詢／下載使用官方 API，並遵守專案作者的 Distribution 設定。
- API Key 不會寫入原始碼、repository、設定或日誌；目前由使用者在需要該次操作時提供並只在記憶體中暫存。
- MCSV 不重新託管第三方模組包，並在介面顯示來源、專案與作者資訊。
- 使用者仍須遵守 Minecraft EULA、平台服務條款及各模組／模組包授權。

Muhun MCSV Manager 是獨立開發的專案，不隸屬於、未獲 Microsoft、Mojang Studios、Modrinth、CurseForge、Feed The Beast、Eclipse Adoptium、Tailscale、Cloudflare 或 Discord 背書。

## 安全

- Windows Service 是受管理狀態的唯一寫入者。
- IPC、REST、事件、更新及 Provider 操作都由 Service 重新授權。
- 密碼、Tunnel token、Discord Webhook 與其他秘密必須保存於 DPAPI Vault，不得進入命令列、日誌、API、Provider manifest 或 Git。
- 私鑰、PFX、Android keystore、Tunnel token、Webhook URL、玩家資料、世界、備份及伺服器日誌不得提交至 repository。
- 安全問題請依 [SECURITY.md](SECURITY.md) 私下回報，不要在公開 Issue 張貼秘密或未遮蔽日誌。

## 目前限制

- 僅正式支援 Windows 10／11 x64。
- 公開的 GitHub 更新 feed 尚未部署前，GUI 自動下載更新不可使用。
- iOS 版本是可加入主畫面的 PWA，不是 App Store 原生 IPA。
- Android 版本是 HTTPS WebView shell，目前不是 Google Play 發行。
- 尚未提供「玩家連線時喚醒、無人時自動關閉」功能。
- 模組包更新可保護檔案並回復，但無法保證任意第三方模組跨版本的語意相容性。

## 文件

- [1.0.6 使用說明](docs/使用說明-1.0.6.md)
- [1.0.6 測試報告](docs/測試報告-1.0.6.md)
- [正式產品架構](docs/正式產品-架構-1.0.md)
- [線上模組包目錄](docs/正式產品-線上模組包目錄.md)
- [第三最終階段驗收矩陣](docs/正式產品-第三階段-Roadmap.md)
- [第三階段完成報告](docs/正式產品-第三階段-完成報告.md)
- [安全政策](SECURITY.md)
- [第三方授權聲明](THIRD-PARTY-NOTICES.txt)

## 授權狀態

本 repository 的專案本體採「保留所有權利」方式公開檢視；除第三方元件各自授權明確允許的範圍外，未經權利人書面許可，不表示允許使用、修改、重製或散布本專案。詳見 [LICENSE](LICENSE)。
