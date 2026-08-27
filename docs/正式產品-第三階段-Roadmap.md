# Muhun MCSV Manager 1.0：第三最終階段 17 項驗收

更新日期：2026-08-27

## 正式產品邊界

Muhun MCSV Manager 1.0 採 Windows Service 唯一寫入者架構。Minecraft 程序、Port、Console、備份、模組包更新、遠端帳號、權限、通知、Provider 與產品更新都由 Service 持有；Windows GUI、Web、iOS PWA 與 Android APK 是同一套授權 API 的客戶端。

正式切換必須同時維持以下不變條件：

1. 關閉或重開 GUI 不得終止 Service 所持有的 Minecraft Server、Web、通知或更新工作。
2. GUI、Web 與行動端隱藏按鈕都不構成授權；每個 IPC／REST mutation 必須由 Service 重新驗證權限。
3. 遷移採複本、驗證、提交；不就地覆寫歷史 Preview 資料，也不操作使用中的 `D:\Example-MCSV`。
4. Web 只監聽 loopback；公開流量只可由受控 HTTPS Tunnel 代理進入，不開 `0.0.0.0`、UPnP 或路由器 Port Forward。
5. 更新、遷移、備份、Provider 與通知必須有界、可稽核，且在中斷後能安全繼續、取消或回復。

## 17 項正式驗收矩陣

| # | 正式項目 | 產品實作 | 必須通過的驗收 |
|---:|---|---|---|
| 1 | Windows Service | `MuhunMCSV` SCM 服務、`NT SERVICE\MuhunMCSV` 最小權限帳號、復原策略、ProgramData ACL、版本化啟用指標 | 安裝／升級／解除安裝腳本契約、非 LocalSystem、GUI 關閉後服務持續、錯誤切換可回復 |
| 2 | 伺服器執行核心 | Service 擁有 Registry、Java 程序、stdin/stdout/stderr、Console journal、資源取樣、Port 配置、desired-run recovery | 同時啟動不撞 Port、`25565` 釋放後可重用、慢訂閱者不阻塞、Service 重啟恢復意圖、停止與移除安全 |
| 3 | Windows GUI | WPF 只以具 ACL 的 Named Pipe IPC API 1.5 操作 Service；支援清單、狀態、Console、玩家、設定、備份、匯入、模組包與更新 | 協定握手不相容時 fail closed、Service 離線不回退成 GUI 直控 Java、主要命令與狀態端到端測試 |
| 4 | Web 遠端面板 | Service 內嵌 responsive Web/PWA，提供總覽、Server、Console、玩家、備份、產品更新 | 桌機／手機版面、SSE/輪詢恢復、API 契約、無權限功能隱藏且後端仍拒絕、離線不排隊 mutation |
| 5 | 使用者與權限 | 多帳號、角色、全域及 Server scope grant、security stamp、記住裝置、最後 Owner 防護、SQLite audit | 預設拒絕、逐 Server 隔離、權限變更撤銷舊 Session、最後 Owner 不可刪除／停權、所有 mutation 留稽核 |
| 6 | HTTPS 遠端連線 | Service 管理固定 Tailscale Funnel 前景 session；另保留可攜相容模式的 Named/Quick Tunnel；所有 Web Host 僅 loopback | 精確 443 route、衝突不覆寫、非預期 schema fail closed、Tunnel 中止撤銷入口、Cookie/Origin/CSRF 安全 |
| 7 | 手機／PWA／iOS／APK | iOS 可加入主畫面；Android WebView shell 只載入 HTTPS 固定來源、保存 HttpOnly 裝置授權而非密碼 | Safari/Chrome/PWA 契約、斷線有界退避、無 cleartext、APK v2/v3/v4 簽章與 package/version/cert pin 驗證 |
| 8 | 通知系統 | 版本化 domain event、SQLite outbox、節流、去重、重試、歷史紀錄；GUI 可設定訂閱 | crash 後重送、敏感欄位拒絕、同事件去重、失敗重試有界、通知狀態由 Service 持有 |
| 9 | Discord Webhook | Webhook URL 只存 DPAPI Vault；限制 Discord 官方 host、redirect、timeout、payload 與重試策略 | 429 尊重 Retry-After、5xx 有界退避、401/404 停用、token 不進 log/API/event/audit |
| 10 | 線上模組包目錄 | Modrinth／CurseForge／FTB 來源、排序、Minecraft 版本、Loader、分類、預覽圖、版本列與背景工作 | 每個篩選映射 Provider 查詢、快取與圖片解碼有界、缺圖一致降級、安裝後清單圖示與來源身分持久化 |
| 11 | 模組包疊代更新與回復 | GUI 下載候選後只送入受授權 imports；Service 驗證來源、建立不覆蓋備份、套用、啟動健康檢查、自動回復 | 世界／玩家資料保留、核心不納入更新備份、hash/路徑/重解析點檢查、crash journal 恢復、健康失敗自動 rollback |
| 12 | GUI／Service 自動更新 | stable/beta、固定 HTTPS host、公鑰 pin、RSA-PSS manifest、整包與逐檔 SHA-256、安全解壓、外部 Updater A/B 切換 | 版本／通道／RID／host 驗證、zip bomb/traversal/重複檔拒絕、一次性啟用請求、Service/GUI 健康失敗切回舊版 |
| 13 | 多語言 | WPF 與 Web 共用版本化 `zh-TW`／`en-US` catalog；Service 回傳穩定 code/key/arguments | 兩語系鍵值完全相同、格式參數一致、切換即時刷新、正式畫面不得依翻譯文字判斷流程 |
| 14 | Plugin Provider | 簽署 `.mcsvp`、Provider registry、Publisher trust、獨立程序 RPC、能力與 host allowlist、Windows Job 限制 | 私鑰不進套件、ECDSA 簽章／逐檔 hash、逾時／輸出／資源有界、未知能力與網路 host 預設拒絕、程序失敗隔離 |
| 15 | 正式深色外觀 | 一致深色資源、黑金等主題、即時字體／視窗大小預覽、伺服器背景與 ICON、深色自訂訊息框、原生 HWND 深色 surface | 所有 Window 套用同一 Style、PasswordBox 深色、resize／卡頓／首幀以 DWM 與 `WM_ERASEBKGND` 避免白底閃現 |
| 16 | 效能與穩定性 | Console 100 ms 批次、有界 queue、單次 Reset、玩家 lazy-load/cancel、presence 合併、資源 latest-only、背景工作自動清除成功紀錄 | 大量 Console／玩家／狀態輸入不逐筆塞 UI Dispatcher、記憶體有界、切換 Server 不阻塞、取消與關閉安全收斂 |
| 17 | 安裝、遷移、簽章與正式驗收 | locked restore、warnings-as-errors、十個測試專案、self-contained win-x64、Authenticode、RSA-PSS、Provider ECDSA、版本化安裝及回復 | 全新／升級／回復腳本驗證、私鑰在 repo 外、正式 manifest/ZIP/APK 全部重驗、SHA-256 報告、不可修改歷史使用中資料 |

## 正式發行門檻

正式產物只有在以下條件全部成立後才可交付：

- Source version、GUI、Service、Updater、Provider、manifest 與安裝目錄皆為相同的最終 SemVer。
- `dotnet restore --locked-mode`、Release solution build 及十個測試專案全部成功，且 warning/error 為零。
- Windows GUI、Service、Updater、Provider EXE 與管理腳本都有相同發布者的 SHA-256 Authenticode 與時間戳。
- 更新與 release manifest 通過 pinned RSA-PSS-SHA256；Provider 套件通過獨立 ECDSA P-256 簽章。
- Android APK 通過 pinned Build Tools 的 package、version、唯一憑證與 v2/v3（及工具支援的 v4）驗證。
- 低階發行驗證器從磁碟重新讀取 ZIP、manifest、逐檔 hash、簽章與安裝腳本，不採信建置程序記憶體中的結果。
- 安裝與遷移測試只能使用隔離測試目錄；不得註冊或覆寫正在使用的歷史 `D:\Example-MCSV`。

## 外部信任限制

專案可自行產生並使用本機自簽 Code Signing 憑證，證明同一把專案私鑰及檔案完整性；它不等於公開 CA，也不會自動取得 Microsoft SmartScreen 信譽。公開散發若要求 Windows 預設信任，仍需由公開 Code Signing CA 核發憑證。Tailscale、Cloudflare、Discord 與公開更新網站的可用性亦受第三方帳號與服務條款約束；程式不會宣稱第三方網址永久、無限制或永不失效。
