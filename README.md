# X MCSV

X MCSV 是為 Windows 10／11 x64 設計的自架 Minecraft 多伺服器與客戶端管理工具。目前 repository 的來源快照版本為 **1.2.7**。

Server 管理採用「Windows Service 唯一寫入者」架構：Server 程序、Port、控制台、備份、模組包更新、遠端帳號、權限、通知、Provider 與產品更新都由背景 Service 統一管理；Windows GUI、Web／PWA 與 Android 客戶端只透過受授權的版本化介面操作。互動式 Minecraft Java 客戶端則在目前登入的 Windows 使用者 Session 中執行，不取得 Service 權限。

> **發行狀態：研發中。** repository 目前只透過 [GitHub Releases](https://github.com/llngh39690619-maker/Muhun-MCSV-Manager/releases) 發布 1.2.7 原始碼與技術文件，不提供 Windows 安裝包、可執行檔、APK、簽章或其他二進位成品。GitHub 自動產生的 Source code ZIP／tar.gz 只是原始碼快照，不能直接當作安裝包使用。

## English summary

X MCSV is a self-hosted Windows desktop and web-based Minecraft server and client manager. It combines a least-privilege Windows Service, WPF desktop GUI, responsive Web/PWA panel, role-based access control, backups, modpack workflows, notifications, provider isolation, and secure HTTPS remote administration.

The server-side CurseForge catalog uses the official API with a user-supplied, in-memory API key, respects each author's third-party distribution setting, attributes projects and authors, avoids rehosting files, and bounds requests through caching and query limits. The Java client workspace does not scrape CurseForge or embed a CurseForge API key. No production credential is committed to this repository.

## 主要功能

- 建立、匯入、啟動、停止、重新啟動及批次管理多個 Minecraft Server。
- 建立與啟動隔離的 Minecraft Java 客戶端；只列 Mojang 正式 release，支援 Vanilla、Fabric、Forge、NeoForge、Quilt。OptiFine／LabyMod 僅交給各自的官方外部安裝流程，不靜默下載或鏡像檔案。
- 基岩版可建立自訂 X MCSV 本機顯示名稱，並選擇 Microsoft 官方正式版最新版或預覽版最新版通道；它使用獨立捷徑登錄，不建立受管理 Java 實例，也不碰觸世界或 Store 安裝資料。Microsoft 不提供任意歷史 Bedrock 版本的公開消費者下載流程，因此介面不會偽裝成可鎖定或下載舊版。
- Microsoft OAuth／裝置碼登入、Minecraft Java 擁有權檢查、玩家資料與權杖自動續期；token 僅以目前 Windows 使用者的 DPAPI vault 保存，不要求或保存 Microsoft 密碼。
- 客戶端 Java 自動準備、全域／自動／手動記憶體、解析度、全螢幕、快速啟動、系統匣、GPU 偏好及有界即時日誌。
- 客戶端模組、材質包、光影包、地圖與截圖管理；Modrinth 內容搜尋會鎖定實例遊戲版本、驗證正式穩定版本與檔案雜湊、遞迴安裝必要前置模組，並在安全時自動匹配 Forge／NeoForge／Fabric／Quilt。客戶端不爬取 CurseForge 網頁，也不把 API Key 寫入 EXE。
- 官方 Skin／披風管理；Skin 支援經典／苗條體型、本機 PNG 上傳、即時 3D 走路預覽與滑鼠 360 度旋轉，保存後同步至 Minecraft 官方服務。
- Minecraft 客戶端在背景啟動 Java，不顯示黑色主控台；啟動後可縮小 X MCSV，遊戲關閉時自動還原主視窗。
- Windows Service 持續持有 Server；關閉 GUI 不會終止 Service 管理中的 Minecraft 程序或已啟用的 Web 服務。
- 深色 WPF GUI，包含控制台、錯誤／警告分流、玩家資訊、備份、Java、模組／插件、外觀與伺服器設定。
- 啟動時從 `25565` 起選擇最低可用 TCP Port，並以保留機制避免同時啟動時發生競爭；目前支援 `server.properties` 類型核心與 Velocity，BungeeCord／Waterfall 在安全 YAML 編輯支援完成前會明確拒絕啟動。
- Server 模組包目錄支援 Modrinth、FTB 與 CurseForge BYOK；客戶端目錄支援 Modrinth 驗證安裝、FTB 公開正式版直接安裝與官方 App 備援，以及搜尋、排序、遊戲版本、Loader、分類與預覽圖。
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
       ├─ 目前 Windows 使用者 Session：隔離 Java 客戶端／DPAPI 帳號 Vault
       │
       └─ ACL 保護的 Named Pipe IPC API 1.5
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
├─ MinecraftServerManager.GameClient/       Minecraft Java 客戶端安裝、登入、啟動與內容管理
├─ MinecraftServerManager.GameClient.Contracts/ 客戶端持久化與目錄契約
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

### 使用既有正式發行包（不適用於 1.2.7 原始碼快照）

- 安裝／升級 Windows Service 時需要系統管理員權限。
- 正式 Windows 執行檔為 self-contained，不需另行安裝 .NET Runtime。
- 正式發行目錄不是 portable 版；必須執行其中已簽署的 `Install-MuhunMcsv.ps1`，不能把 ZIP 解壓後只雙擊某一個 EXE 當成完整安裝。
- Minecraft Server 仍須使用符合其版本與 Loader 要求的 Java。
- Minecraft 客戶端可由 X MCSV 按遊戲版本自動下載並驗證 Eclipse Adoptium Java；也可在實例設定中指定既有 Java。

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

正式發行流程包含 self-contained publish、Windows／Provider／APK 簽章、RSA-PSS manifest、逐檔 SHA-256、封裝及獨立磁碟驗證。最近一次已公開記錄的完整結果見 [1.1.0 正式測試報告](docs/測試報告-1.1.0.md)，流程見[正式簽章與安全發布](docs/正式產品-簽章與安全發布.md)。

## Web 與手機管理

1. 在桌面 GUI 建立遠端帳號。
2. 為每個帳號設定全域及逐 Server 權限。
3. 設定 Tailscale Funnel，或使用 Cloudflare Named／Quick Tunnel 相容模式。
4. 從 HTTPS 網址登入 Web 面板。
5. iOS 可使用 Safari「加入主畫面」；Android 可側載既有正式發行包中的簽署 APK（1.2.7 的 GitHub 發布不提供 APK）。

遠端後端會重新檢查登入狀態、角色、Server scope、Origin、CSRF 與 Idempotency-Key；前端隱藏按鈕不被視為安全授權。

## CurseForge 與第三方內容

- Server 端 CurseForge 查詢／下載使用官方 API，並遵守專案作者的 Distribution 設定。
- CurseForge API Key 不會寫入原始碼、repository、設定或日誌；由使用者在需要該次 Server 端操作時提供，並只在該次流程的記憶體中暫存。
- 客戶端工作區不把 CurseForge API Key 寫入 EXE，也不爬取網頁；免金鑰安裝使用 Modrinth 或 FTB 官方公開 API。FTB 只接受公開正式穩定版，逐檔驗證官方 manifest 的 SHA-512／SHA-256／SHA-1，官方 App 保留為失敗備援。
- X MCSV 不重新託管第三方模組包，並在介面顯示來源、專案與作者資訊。
- 使用者仍須遵守 Minecraft EULA、平台服務條款及各模組／模組包授權。

X MCSV 是獨立開發的專案，不隸屬於、未獲 Microsoft、Mojang Studios、Modrinth、CurseForge、Feed The Beast、Eclipse Adoptium、Tailscale、Cloudflare 或 Discord 背書。

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
- 基岩版只提供自訂名稱的本機捷徑，以及 Microsoft 官方最新版／預覽版通道交接；不由 X MCSV 下載、安裝、鎖定歷史版本或管理為 Java 實例。
- 尚未提供「玩家連線時喚醒、無人時自動關閉」功能。
- 模組包更新可保護檔案並回復，但無法保證任意第三方模組跨版本的語意相容性。

## 文件

- [1.1.0 使用說明](docs/使用說明-1.1.0.md)
- [1.1.0 正式測試報告](docs/測試報告-1.1.0.md)
- [正式產品架構](docs/正式產品-架構-1.0.md)
- [線上模組包目錄](docs/正式產品-線上模組包目錄.md)
- [第三最終階段驗收矩陣](docs/正式產品-第三階段-Roadmap.md)
- [第三階段完成報告](docs/正式產品-第三階段-完成報告.md)
- [安全政策](SECURITY.md)
- [第三方授權聲明](THIRD-PARTY-NOTICES.txt)

## 授權狀態

本 repository 的專案本體採「保留所有權利」方式公開檢視；除第三方元件各自授權明確允許的範圍外，未經權利人書面許可，不表示允許使用、修改、重製或散布本專案。詳見 [LICENSE](LICENSE)。
