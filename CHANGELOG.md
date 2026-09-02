# Changelog

## 未發布

## 1.2.9-beta.10 — 客戶端下載穩定性與安裝狀態修正版（研發中）

- 修正 FTB 客戶端安裝的子階段進度直接被當成整體進度、失敗後又收到延遲 100% 回報的問題；基礎遊戲、Loader、模組包內容與安全啟用改用分段權重，只有完整驗證並啟用成功才顯示 100%，失敗工作會保留實際失敗階段、紅色狀態與診斷資料夾入口。
- 下載器預設並行數由 8 降至 4，位元組進度採 1 MiB／125 ms 節流，移除每個小區塊都強制同步落盤的額外負擔；檔案大小與雜湊驗證、可取消重試、耐久寫入及原子替換仍完整保留，降低大型模組包下載期間的 CPU、磁碟與 UI 更新尖峰。
- FTB 工作改用獨立於原版與 Modrinth 的專屬 staging 子目錄；復原只會在這個 FTB 專屬範圍內辨識沒有 receipt 的 32 位十六進位暫存工作，並只回收非連結／reparse point 且通過路徑、Registry 與 identity 邊界驗證的遺留目錄。升級時仍會依 beta.9 舊根中的精確 FTB receipt 復原，但絕不把舊根其他 GUID 目錄當成 FTB 資料刪除。
- NeoForge／Forge 官方 Loader 程序以 best-effort 較低優先權執行；失敗診斷會保存安全化的退出碼、穩定失敗分類與可用的 installer log 尾端，並相容安裝根目錄含空白的路徑。正式 profile 仍採原本的安全 lease 與就地寫入，不放寬檔案身分驗證。
- 本機 Android 驗證建置使用 `versionCode 29`；本版仍為 Beta／研發中。完整本機發行驗證會產生單一 installer EXE，但 GitHub Release 只發布原始碼與文件，不附上 EXE、安裝包、APK、簽章、雜湊或其他二進位成品。

## 1.2.9-beta.9 — 單一安裝器與安裝根目錄整合版（研發中）

- Windows 發行流程改為建立單一安裝 EXE；安裝器會顯示 UAC 系統管理員確認，預設安裝至 `C:\Program Files\MCSV`，也可在安裝畫面選擇其他安全的本機位置，不再要求使用者以 PowerShell 手動展開或安裝正式產品。
- 修正安裝工作經過非同步 I/O 後在不同執行緒釋放 Windows Mutex、導致安裝失敗訊息被同步例外覆蓋的問題；全域安裝鎖改由專用背景執行緒持有及釋放，保留程序異常結束後的 abandoned-mutex 復原，並僅允許 Administrators 與 SYSTEM 控制。
- 修正安裝器持有安裝目錄或既有 `active-version.v1` 的安全 handle 時阻擋自身原子搬移、造成 3% 進度出現檔案使用中錯誤的問題；新建目錄與既有目錄採相符的 lease 分享模式，版本指標替換前會驗證後釋放檔案 handle，若後續 ACL 步驟失敗仍會回復舊版本指標與權限。
- 新解壓的程式若正被 Windows Defender 或其他防毒軟體短暫掃描，啟用版本目錄會在保留安全 lease、逐次重驗來源與目的地身分的前提下做有限且可取消的原子搬移重試；不會停用防護、建立排除項目或退回非原子的複製覆寫。
- 修正全新電腦安裝後 Windows Service 雖顯示執行中，卻因缺少 `Muhun MCSV Operators` 與安裝者 SID 綁定而永遠無法通過 activation-ready、最終被安裝器回復的問題；單一 EXE 現在會以原生 Windows API 建立受管理群組、精確加入目前使用者、原子寫入 IPC 綁定，並直接套用及驗證僅允許 SYSTEM／Administrators 完全控制與 Service 讀取的受保護 ACL；任一步失敗都會保守還原既有內容、ACL、會員與群組，不需呼叫 PowerShell。
- activation-ready 現在可透過既有本機權杖驗證端點回傳白名單化的 IPC 啟動診斷代碼、例外型別與 HRESULT，安裝器不再只留下無法判讀的通用逾時；診斷不包含例外訊息、路徑、SID、權杖或堆疊，IPC 恢復後也會立即清除舊失敗狀態。
- 修正 Windows 將受保護 DACL 正規化為 `PAI` 後，被安裝器誤判成 SID 綁定檔 ACL 遭竄改、導致安裝與回復同時失敗的問題；驗證只容許系統加入 `DACL Auto-Inherited` 旗標，其餘控制旗標、Owner、Group 與原始 ACL 位元組仍須完全一致，也會拒絕額外 Object ACE。上一輪失敗若只留下預設位置的空白管理員專用目錄，新安裝器會在雙重驗證預設路徑、空目錄、非 reparse point 與精確 ACL 後安全接續，其他未標記目錄仍一律拒絕。
- 程式版本、Windows Service 資料、GUI／Service 交換區與每位 Windows 使用者的 GUI／Minecraft 客戶端資料，統一綁定在同一個已選安裝根目錄下；Beta 版分別使用 `versions`、`service\beta`、`exchange\beta` 與 `users\<Windows SID>\beta`，更新及回復不會把資料分散到其他磁碟位置。
- 正式 GUI 只接受安裝器建立、具有產品 ownership marker、有效 `active-version.v1` 及相符 installed-version metadata 的目前啟用版本；複製出來的單獨 EXE、未啟用的舊版本或不完整發行目錄都會安全停止並要求修復，不會自行建立另一套資料。
- 移除正式執行模式對 `%LocalAppData%` 與 `%ProgramData%` 的資料根 fallback；已選安裝根目錄缺失、權限不符或路徑身分驗證失敗時會 fail closed，避免再次產生重複的伺服器、客戶端、Runtime、快取與備份。
- 現有 `D:\MCSV` 視為受保護的既有資料，安裝、修復、測試、清理與更新流程都不得掃描後刪除、搬移、覆寫或拿來當暫存目錄；本機測試及建置輸出只保留在 Codex／repository 工作流程目錄內。
- 本機 Android 驗證建置使用 `versionCode 28`；本版仍為 Beta／研發中。完整本機發行驗證會產生單一 installer EXE 供安裝流程測試，但 GitHub Release 只發布原始碼與文件，不附上 EXE、安裝包、APK、簽章、雜湊或其他二進位成品，避免不知情使用者下載研發版本。

## 1.2.9-beta.8 — Service 已知玩家顯示修正版（研發中）

- 修正 Windows Service 管理實例只能取得目前線上玩家、勾選「顯示已知玩家（含離線）」後仍無法顯示 `usercache.json`、OP、白名單與封禁名單中已知玩家的問題。
- Service 以受管理實例身分安全讀取有界玩家登錄資料，並透過版本化 IPC 分別回傳線上與已知玩家；GUI 會保留舊版 Service 的線上名單相容性，重新整理後同步更新離線玩家與角色狀態。
- 本機 Android 驗證建置使用 `versionCode 27`；本版仍為 Beta，僅在 GitHub 發布原始碼並標示為研發中，不上傳 Windows 安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.9-beta.7 — 背景服務更新與啟動狀態修正版（研發中）

- 修正本機完整發行資料夾的「更新背景服務」會拒絕 GitHub 發行網址、導致 Service 一直停留在舊版的問題；正式修復信任清單現在精確接受已簽署 manifest 使用的 `github.com` 與既有 tailnet 主機，仍會拒絕其他來源。
- Service 版本切換會依序確認停止、重新註冊、實際 `ImagePath`／DataRoot／服務帳號／啟動類型、重新啟動及目前 IPC API 健康狀態；驗證、寫入、回復等失敗階段會顯示穩定結果，失敗後重新探測既有 Service 並以受保護 broker 清理暫存副本。
- 伺服器啟動前會在實際 Service 工作目錄建立缺少的 `server.properties`，或只更新既有檔案的 `server-port` 並保留註解與未知欄位；從保存的連接埠向上跳過其他 TCP listener，UDP 不會誤占 Minecraft 主連接埠，最終值同步至 Registry、API 與 GUI。
- Java 狀態優先顯示受管理 Runtime `release` 檔提供的完整版本（例如 `Java 21.0.10`）；與舊版 API 相容時會保留已驗證或匯入的 Java 主版本，不再因受管理路徑不含版號而退回「未指定」。
- 本版為 Beta，僅在 GitHub 發布原始碼並標示為研發中；正式簽署 Windows 與 Android 建置只留作本機驗證，不上傳安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.9-beta.6 — 執行效能與狀態可視化修正版（研發中）

- Service 狀態會直接回傳由實際 Java `release` 檔解析的版本、供應商、架構與 Runtime 類型；GUI 不再從受管理目錄名稱猜測版本，因此 Windows Service 實例可正確顯示 `Java 21` 等有效版本，不再固定顯示「未指定」。
- 伺服器啟動前若連接埠與啟動參數沒有變更，不再重寫並強制落盤整份 Service Registry；連接埠配置只查詢 Minecraft 主連線需要的 TCP listener，Java metadata 與 listener 快照也採短期共用快取，降低大型實例的啟動等待。
- 連接埠卡片會顯示 Windows 在該 Port 觀測到的「監聽中／未監聽」就緒診斷，並以 `localhost:連接埠` 作為完整提示；保存的 `25566` 仍是啟動搜尋起點，只有真正被其他 TCP listener 占用時才向上配置，不再受無關 UDP listener 影響。
- 建立完成進入 Service 匯入階段時會立即加入停用中的待完成清單列，提交成功後只刷新該一台伺服器，不再重新載入全部實例；未變更的 Service registration 也不再每兩秒重送整批 UI 屬性通知。
- 客戶端工作區會批次發布本機實例並先恢復操作，Mojang 版本目錄改由背景更新；切到客戶端後不再讀取隱藏的伺服器控制台。已完成且沒有待清理工作的 FTB receipt 只驗證實例根目錄身分，不再於每次開啟時遞迴掃描大型模組包兩遍。
- Windows、測試隔離桌面與 Android 建置流程統一使用較低程序優先權、有限並行數與跨流程互斥；solution 只建置一次、測試不再重複 build／restore，Android 保留增量快取且只移除預期輸出，維持測試、Lint、簽章與產物驗證的同時減少遊戲中的電腦卡頓。
- 本機 IPC 提升至 API 1.9；本版為 Beta，僅在 GitHub 發布原始碼並標示為研發中，不上傳 Windows 安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.9-beta.5 — Service 實例設定與相容性修正版（研發中）

- 修正已安裝的舊版 Windows Service 雖只支援 API 1.6，卻仍被視為已連線而隱藏「更新背景服務」動作的問題；現在已連線但缺少新版能力時也會明確提示更新，完成更新並重新連線後會自動載入 `server.properties`。
- Service 管理實例的記憶體模式、有效記憶體範圍、獨立診斷輸出、自動重啟、Watchdog 與健康恢復點設定現在可完整往返保存；只有伺服器精確處於 `Stopped` 時才允許編輯及儲存，避免執行中草稿被背景快照覆蓋。
- 「使用預設」會套用目前的新伺服器預設值；「自動」改用 Service 提供的有界、無路徑模組資料估算，不再讀取假的 GUI 投影目錄，資料清單截斷時會採保守最高級距，計算失敗則拒絕誤存。
- `server.properties` 區塊新增斷線、需要更新、載入中、尚待重新讀取及暫時讀取失敗等內嵌狀態；暫時失敗後「重新讀取」仍可再次使用，成功後恢復編輯與儲存。
- 本機 IPC 提升至 API 1.8；新版設定欄位會把最低要求同步提升至 1.8，API 1.7 對任何新版欄位都會明確拒絕而不會靜默忽略，同時保留 API 1.7 的 `server.properties` 能力及舊格式設定相容性。
- 本版為 Beta，僅在 GitHub 發布原始碼並標示為研發中；正式簽署 Windows 與 Android 建置只留作本機驗證，不上傳安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.9-beta.4 — 服務設定與連接埠同步修正版（研發中）

- 修正 Windows Service 管理實例的 `server.properties` 原始編輯器內容空白，且「重新讀取」與儲存按鈕無法使用的問題；GUI 現在會透過受保護的本機 IPC 自動載入、重新讀取及儲存，不再嘗試存取假的投影路徑或 Service DataRoot。
- 本機 IPC 提升至 API 1.7，新增有界的設定文件讀寫與 SHA-256 修訂衝突保護；儲存會保留原始編碼、BOM、換行及備份，並在落盤後重新讀取確認。舊版 API 1.6 Service 仍可連線，但編輯器會安全停用。
- 修正 Service 已保存 `25566`，啟動時卻固定從 `25565` 重新配置的問題；現在會優先使用使用者保存的連接埠，只有被占用時才向上尋找下一個可用值，並同步實際 `server.properties`、Service 登錄與畫面狀態。
- 設定儲存與伺服器啟動共用每個實例的生命週期鎖，避免啟動前連接埠同步與原始編輯器互相覆寫；IPC 同時限制路徑、reparse point、控制字元、檔案大小與最壞 JSON frame 大小。
- 本版為 Beta，僅在 GitHub 發布原始碼並標示為研發中；正式簽署 Windows 與 Android 建置只留作本機驗證，不上傳安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.9-beta.3 — UTF-8 發行驗證修正版（研發中）

- 修正正式發行包的 `SHA256SUMS.txt` 含 `開始使用.txt` 等中文路徑時，被背景服務修復流程錯誤當成非 ASCII 非法內容，進而誤報為發布者驗證失敗的問題。
- GUI 的受保護暫存驗證與 Updater 的本機正式包驗證統一改用無 BOM、拒絕無效位元組的嚴格 UTF-8；Unicode 路徑仍須同時符合簽署 manifest、逐檔大小、SHA-256、精確檔案集合及安全路徑規則。
- 在乾淨 Windows 未匯入自簽根憑證時，只有固定憑證且已受 RSA-PSS manifest 逐位元組綁定的 Updater，才可接受 `CERT_E_UNTRUSTEDROOT`／`CERT_E_CHAINING`；壞雜湊、錯誤發布者及其他 Authenticode 失敗仍會安全拒絕。
- 本版為 Beta，僅在 GitHub 發布原始碼並標示為研發中；正式簽署 Windows 與 Android 建置只留作本機驗證，不上傳安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.9-beta.2 — 背景服務相容性自助修復版（研發中）

- 當新版 GUI 偵測到舊版 Windows Service 不具 API 1.6 Minecraft EULA 能力時，伺服器操作仍會安全保持停用，並在主視窗底部直接顯示「更新背景服務」，不再只留下版本不相容訊息與一整排灰色按鈕。
- 完整正式發行資料夾可由 GUI 啟動同版本、已簽署的 Updater；更新前會驗證固定發布者憑證與公鑰、RSA-PSS 發行／更新 manifest、完整檔案清單、逐檔大小與 SHA-256、產品版本、路徑及 reparse point，無須 PowerShell 7，也不會把 Service 指向使用者可寫的文件資料夾。
- 通過 Windows 系統管理員提示後，新版會先複製到受保護的 `Program Files` staging，再以 A/B active pointer 切換並重新啟動 Service；只有安裝識別、目標版本與 API 1.6 handshake 都通過才提交，否則自動回復舊版。相同版本與任何 SemVer 降版均會拒絕。
- 服務不相容或暫時中斷時，批次啟停、備份、主控台輸入及玩家管理都會同步停用；即使畫面保留先前的 Running 投影也不會誤送命令，服務修復後則自動恢復符合目前狀態的操作。
- 本版為 Beta，僅在 GitHub 發布原始碼並標示為研發中；正式簽署 Windows 與 Android 建置只留作本機驗證，不上傳安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.9-beta.1 — 主螢幕定位與 EULA 啟動前檢查修正版（研發中）

- X MCSV 主視窗固定在 Windows 主要顯示器置中開啟；由 X MCSV 開啟的子視窗與檔案選擇器跟隨所屬視窗，螢幕配置變更後會回到可見工作區，同時保留使用者開啟後自由移動視窗的行為。
- 所有會建立 WPF 視窗的自動化測試都必須在獨立、非互動的 Windows desktop 內執行；隔離失敗時直接停止，不會退回玩家正在遊戲的桌面，也不再跳到第二螢幕或搶走焦點。
- Minecraft 核心在建立與每次啟動前都會於受鎖定的實例目錄檢查 `eula.txt`；只有使用者在建立介面勾選，或於手動啟動／重新啟動時明確確認後，才會以安全寫入流程設定並再次驗證 `eula=true`。
- Windows Service、GUI 直接啟動與既有 Paper／Vanilla 實例共用相同的 EULA 啟動前檢查；遠端啟動、自動重啟與服務復原不會暗中接受 EULA，代理伺服器核心則維持不需要 Minecraft EULA 的正確行為。
- FTB Server 模組包的新安裝與更新都新增未預勾的 Minecraft EULA 確認與官方連結；只有目前操作明確同意後才會傳遞 `-accept-eula`，背景工作與既有更新流程不再暗中代為接受。
- 本機 Named Pipe IPC 升級為 API 1.6；`AcceptMinecraftEula` 只能在協商到 1.6 後用於單次啟動／重新啟動，新 GUI 會將不具此能力的舊 Service 判定為不相容，避免同意旗標被靜默忽略。
- 本版為 Beta，僅在 GitHub 發布原始碼並標示為研發中；正式簽署 Windows 與 Android 建置只留作本機驗證，不上傳安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.8 — 客戶端實例自動命名修正版（研發中）

- 原版實例名稱會隨遊戲版本更新為 `Minecraft 版本`；Fabric、Forge、Quilt、NeoForge、OptiFine 與 LabyMod 則更新為 `載入器 版本`，不把載入器的具體建置版號加入名稱。
- 使用者一旦實際修改或清空實例名稱，本次建立流程後續切換遊戲版本、載入器或載入器建置版本都不會覆蓋自訂內容；關閉再重新開啟建立頁時才恢復自動命名。
- 重新按下目前已開啟的建立頁不會重設手動名稱；載入器清單非同步刷新時也會抑制內部暫時選擇造成的名稱跳動，完成後只套用最終可用載入器。
- 本版僅在 GitHub 發布原始碼並標示為研發中；正式簽署 Windows 與 Android 建置只留作本機驗證，不上傳安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.7 — 內容下載中心版面與導覽修正版（研發中）

- 模組、材質包與光影包的「下載資訊」會直接開啟對應的下載中心分頁；視窗已開啟時也會切換到指定項目並帶到前景，不再一律停留在模組頁。
- 內容詳情的「可安裝正式版本」選單固定在右側最上方；圖示、介紹、相容資訊、依賴與備援內容改為中段獨立捲動，選擇安裝版本不必再捲到介紹末端。
- 「開啟官方專案」與「下載並安裝」固定在詳情底部，字體、按鈕高度、寬度與間距依 16:9 內容下載中心預覽統一；全域最底列專注顯示背景下載工作與進度。
- 本版僅在 GitHub 發布原始碼並標示為研發中；正式簽署 Windows 與 Android 建置只留作本機驗證，不上傳安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.6 — 內容下載中心整合版（研發中）

- 模組、材質包與光影包的「下載資訊」統一開啟獨立 16:9 內容下載中心；提供搜尋、載入器、分類、排序、圖示與相容資訊，結果清單接近底部時自動載入下一頁，不再以 20 筆作為總結果上限。
- 點選內容後顯示官方完整介紹、作者、相容版本、可安裝版本、必要前置內容與安全備援網址；版本選擇會綁定到實際安裝計畫，不再只安裝搜尋當下的預設最新版。
- 安裝目標在視窗開啟時固定為當時的客戶端實例；即使主畫面切換客戶端也不會誤裝。下載、驗證、必要載入器切換與原子匯入在背景佇列執行，視窗底部固定顯示進度，並可取消、展開或清除已完成工作。
- 模組會遞迴匹配必要前置模組及 Forge、NeoForge、Fabric 或 Quilt；材質包與光影包則安裝至對應實例資料夾。只有完整性與相容性驗證通過才寫入正式內容，無法安全自動完成時提供官方專案或直接下載網址。
- 本版僅在 GitHub 發布原始碼並標示為研發中；正式簽署 Windows 與 Android 建置只留作本機驗證，不上傳安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.5 — 模組包詳情與背景安裝列修正版（研發中）

- 模組包卡片會開啟獨立詳情頁，顯示完整介紹、來源、版本、載入器與實例名稱；Modrinth 會額外取得官方完整介紹，若介紹服務暫時失敗仍可使用既有摘要、選擇版本並繼續安裝。
- 「下載並建立」固定在模組包詳情底部，不必捲到介紹末端；下載與建立進度固定在整個客戶端視窗底部並可跨頁面持續顯示，支援取消、展開階段清單，以及清除完成、失敗或取消的紀錄。
- 修正現代 NeoForge 官方安裝成功後仍被舊式 Maven library 規則誤判失敗的問題；新版會驗證官方實際產生的 profile 身分、Minecraft 繼承版本、BootstrapLauncher、FML 版本參數及必要啟動 library，NeoForge 1.20.1 與 Forge 則維持舊版精確 library 驗證。
- 頂部帳號區縮短高度、放大玩家名稱並保留高畫質頭像、玩家名稱下拉選單與新增 Microsoft 帳號按鈕，長名稱也會為下拉箭頭保留足夠空間。
- 本版僅在 GitHub 發布原始碼並標示為研發中；正式簽署 Windows 與 Android 建置只留作本機驗證，不上傳安裝包、可執行檔、APK、簽章、雜湊或其他二進位成品。

## 1.2.4 — FTB 載入器相容性與模組包捲動修正版（研發中）

- 模組包搜尋結果不再攔截外層頁面的滑鼠滾輪；游標位於卡片、文字或結果清單上方時，也能直接上下捲動完整搜尋頁面。
- 執行官方 Forge／NeoForge 客戶端安裝器前，會在隔離暫存目錄安全建立其必要的最小啟動器設定檔，修正官方安裝器因找不到 `launcher_profiles.json` 而以錯誤代碼結束、導致 FTB 內容無法啟用的問題；玩家不必先開啟 Mojang Launcher。
- 保留 1.2.3 的有界重試、原子下載、完整性驗證、交易回滾與去敏診斷；相容設定檔只會在兩種官方設定檔都不存在時建立，不覆寫既有資料。
- 本版僅在 GitHub 發布原始碼並標示為研發中；正式簽署建置只留作本機驗證，不上傳安裝包、APK、簽章、雜湊或其他二進位成品。

## 1.2.3 — FTB 安裝可靠性與診斷修正版（研發中）

- Minecraft 與相符模組載入器的前置檔案改為相鄰暫存下載；完成大小、Content-Length 與 SHA-1 驗證後才原子替換正式檔，連線中斷、取消或驗證失敗不再留下 0 位元正式檔，也不會先破壞既有有效檔案。
- CmlLib 遊戲檔下載限制為最多八路並行，對逾時、連線中斷、HTTP 408／429／5xx、長度或雜湊不符執行最多四次的有界重試；永久 4xx、磁碟、權限與使用者取消不重試。版本 metadata 會以全新 launcher 重試，Forge／NeoForge 官方 SHA-256 sidecar 與 installer 下載也套用相同原則，但 Java 載入器程序不會重複執行。
- FTB 直接安裝失敗會在使用者日誌目錄建立有界、原子且去敏的結構化診斷紀錄，保存實際階段，以及可安全取得時的官方主機、HTTP 狀態、嘗試次數與錯誤類別；不保存原始例外訊息、堆疊、帳號、Token、Cookie、JWT、完整網址或 Windows 使用者路徑。
- 客戶端介面依網路、逾時、HTTP、磁碟、權限、完整性、Java、模組載入器、回滾與復原狀態顯示本地化原因及診斷編號，並可直接開啟診斷資料夾；官方 FTB App 備援仍保留。
- 本版僅在 GitHub 發布原始碼並標示為研發中；正式簽署建置只留作本機驗證，不上傳安裝包、APK、簽章、雜湊或其他二進位成品。

## 1.2.2 — 帳號、建立頁與基岩版捷徑修正版（研發中）

- 帳號列恢復玩家名稱下拉選單與新增 Microsoft 帳號入口；玩家頭像改為原生 8×8 像素合成並以整數倍最近鄰顯示，避免 DPI 縮放造成模糊。
- 客戶端建立頁改為表單獨立捲動、操作列固定在底部，安裝進度列另置於最底列，不再與「下載並建立」同排。
- 基岩版可建立只屬 X MCSV 的自訂名稱捷徑，並選擇 Microsoft 官方正式版最新版或預覽版最新版通道。捷徑使用獨立登錄，不會進入 Java 實例、碰觸世界或 Store 安裝資料；任意歷史版本仍不會以非官方或繞過授權的方式下載。
- 本版僅在 GitHub 發布原始碼並標示為研發中；正式建置成品只留作本機驗證，不上傳安裝包、APK、簽章或其他二進位檔案。

## 1.2.1 — 客戶端操作與 FTB 安裝修正版

- Skin 預覽改為高解析度像素取樣，只有按住滑鼠左鍵拖曳時才旋轉；「經典／苗條」體型選項固定顯示並同步保存到 Minecraft 官方服務。
- 頂部帳號選擇器縮為玩家頭像；原版實例使用草地磚圖示，模組包顯示安全保存於實例內的對應圖示。
- 客戶端解析度改為單一下拉選單並保留全螢幕選項；遊戲啟動後縮小 X MCSV，遊戲關閉後自動還原。
- FTB 客戶端模組包可透過官方公開 API 與官方檔案來源直接下載；只接受公開正式版本，完整安裝所有非伺服器專用檔案，並核對 Minecraft、載入器、記憶體、路徑、大小與三種雜湊。安裝採隔離暫存、可恢復提交與身分綁定回滾；官方資料不完整或無法安全自動安裝時，仍可改用官方 FTB App。

## 1.2.0 — 客戶端帳號、Skin 與內容下載整合版

- 修正伺服器／客戶端工作區切換與客戶端空白狀態；只有按下「建立客戶端」後才開啟建立介面，所有支援的模組載入器維持固定排列，未支援目前遊戲版本的項目會保留顯示並清楚停用。
- Microsoft Minecraft 帳號整合玩家資料、權杖期限與自動續期；互動登入使用 Microsoft 官方 OAuth／裝置碼流程，不要求 X MCSV 取得或保存 Microsoft 密碼。
- 新增官方 Skin／披風管理。Skin 支援經典／苗條體型、即時 3D 走路預覽、滑鼠 360 度旋轉及本機 PNG 上傳；保存後會送至 Minecraft 官方服務，不是只更換本機圖示。
- 修正 Skin 圖集 UV、外層貼圖、舊版 64×32 鏡射、肩關節與動畫生命週期；走路與滑鼠旋轉使用持續的 WPF 動畫及單一插值更新，避免漂浮貼圖片段、肢體脫離與反覆重建造成的卡頓。
- 客戶端內容名稱統一為「模組／材質包／光影包」，新增 Modrinth 搜尋、相容版本篩選、下載與安裝；只採用正式穩定版本，下載檔案與必要前置模組會驗證雜湊後再原子匯入。
- 安裝模組時會檢查實例的 Minecraft 版本與 Forge、NeoForge、Fabric 或 Quilt；需要不同載入器時先嘗試以官方穩定目錄安全切換，無法自動完成時不寫入不相容檔案，並保留官方專案／下載網址。
- Minecraft Java 程序改為隱藏主控台並持續排空標準輸出與錯誤，不再跳出黑色命令視窗；成功啟動遊戲後可縮小 X MCSV，最後一個受監看的客戶端關閉時自動還原主視窗。
- 修正背景建立完成後停在「正在安全加入管理器」的匯入流程，保留可恢復交易與有界重試，不因暫時性檔案鎖定重複下載或遺失既有資料。

## 1.1.0 — X MCSV 客戶端與伺服器整合版

- 產品顯示名稱更新為 X MCSV；既有 Windows Service、ACL、IPC、檔案路徑、更新身分與相容 EXE 名稱維持不變，升級不搬動或覆寫玩家與 Server 資料。
- 新增隔離的 Minecraft Java 客戶端工作區，只列出 Mojang 正式 release；支援 Vanilla、Fabric、Forge、NeoForge、Quilt 的官方穩定 Loader。OptiFine／LabyMod 僅交給官方外部流程，不爬取、鏡像或靜默下載。
- 新增基岩版官方入口交接；只嘗試固定的 `minecraft://`，失敗時交給固定的 Microsoft Minecraft Launcher Store 頁面。不下載基岩版、不建立本機受管理實例，也不套用 Java 記憶體或 Loader 設定。
- 新增 Microsoft OAuth 互動登入、Minecraft Java 擁有權／Profile 驗證、多帳號選擇與 CurrentUser DPAPI token vault；不要求或保存 Microsoft 密碼。
- 依遊戲版本自動準備 Eclipse Adoptium Java，提供全域預設、自動估算及手動記憶體、解析度、全螢幕、JVM 參數與環境變數；高效能 GPU 偏好只寫入目前使用者的 Windows 圖形設定。
- 客戶端程序以 PID、精確啟動時間及 Java 完整路徑三重身分恢復監看；GUI 關閉不終止遊戲，重開不重複啟動同一實例，執行中禁止改寫會破壞程序身分的設定。
- 新增模組、資源包、光影、地圖與截圖管理；所有匯入、停用、回收、還原及永久刪除都受實例根目錄、重解析點、檔案數與大小限制保護，掃描與縮圖在背景執行並可取消。
- Modrinth 客戶端模組包採官方 API、官方 CDN、SHA-512／SHA-1 與安全 `.mrpack` 解壓；1.1.0 當時的 FTB 客戶端模組包只接受公開正式穩定版，排除 Server-only 並預設排除 optional，逐檔驗證大小及 SHA-512／SHA-256／SHA-1，再以隔離 staging、重解析點防護、原子提升與 registry-last 回滾保護既有資料。1.2.1 起則依新版政策完整安裝所有非伺服器專用檔案（包含 optional）；官方 FTB App 保留為失敗備援。
- 目錄卡片、預覽圖、來源、版本、Loader、分類、排序、搜尋與結果數採響應式版面；來源或條件切換會取消舊請求並立即載入新結果。
- 快速啟動、啟動後縮至系統匣與即時遊戲日誌已整合；日誌以有界 queue、75 ms 批次及最多 2,000 行呈現，避免大量輸出逐行塞入 UI Dispatcher。
- 新客戶端畫面、狀態、驗證與設定完整納入繁體中文／英文即時切換；正式深色 surface、卡片與縮圖管線避免首幀、切換、縮放或資料忙碌時露出白底。
- 正式發行管線納入獨立 GameClient 測試專案，使正式測試專案總數為十一個；CmlLib／XboxAuthNet／WebView2／SQLitePCLRaw 等第三方聲明及隨包授權文件一併納入。1.1.0 使用新的不可變產品與 Provider 版本身分，正式發行目錄仍須透過簽署安裝器安裝，不是 portable EXE。

## 1.0.8 — Server 匯入自動恢復修正版

- 修正核心建置完成後，Windows Service 在 Server／Java Runtime 原子搬移期間遇到暫時性存取拒絕，背景工作會永久停在「正在安全加入管理器」的問題。
- Service 對暫時性目錄搬移失敗進行有界重試，並在同一次服務執行期間以退避方式自動恢復；即使自動重試耗盡，交易與玩家資料仍會完整保留，重新啟動 Service 後可接續。
- 部分搬移或斷電恢復會逐檔驗證檔案集合、大小與 SHA-256，沿用已完成的 Server／Java 工作樹，不重複複製大型 Runtime，也不收編未知或不符 manifest 的目錄。
- 修正目錄已搬移、但 promotion 旗標尚未持久化的崩潰視窗；重啟後會先驗證並記錄已完成的搬移，再繼續註冊，避免誤刪暫存資料或留下未註冊的孤立 Server。
- 桌面端匯入輪詢加入有界停滯偵測：持續 `import.resume_required` 或長時間完全無進度時會顯示可診斷錯誤，不再無限等待；正常驗證、複製及短暫重試會重設期限。
- 正式修正版使用新的 1.0.8 產品與 first-party Provider 版本身分；不以不同內容覆寫先前驗證過的 1.0.7 Provider digest，1.0.6 與較早發行資產保持不變。

## 1.0.6 — GUI 啟用握手與不可變 Provider 修正版

- 正式修正版使用新的 1.0.6 產品與 first-party Provider 版本身分，避免將 GUI readiness race 修正後的不同簽章內容重新發布為既有 1.0.5 digest；1.0.4 與 1.0.5 歷史資產維持不變。
- GUI 啟用健康檢查會並行接收 readiness acknowledgement 與互動穩定性驗證，避免 GUI 很快回報 ready 時，broker 尚未開始接收而誤判逾時；失敗及取消路徑會有界取消並觀察背景驗證工作。
- 完整承接 1.0.5 的安裝交易修正，以及 1.0.4 的啟動 Port、BuildTools cache、線上模組包、遠端入口與視窗尺寸修正。

## 1.0.5 — 安裝交易與不可變 Provider 修正版

- 正式修正版使用新的 1.0.5 產品與 first-party Provider 版本身分，避免把內容不同的簽章 `.mcsvp` 重新發布成既有 1.0.4 版本而觸發不可變 digest 衝突；1.0.4 歷史資產與紀錄維持不變。
- 安裝升級可對逾時停止的舊 Service 做有界等待，並在已存在同版部分建置目錄時驗證、隔離後安全重試，不以未完成內容冒充可啟用版本。
- 安裝失敗回復會保留原 Service 設定、failure actions、active pointer 與 stable launcher，並改善受鎖定版本／launcher 的延後清理及錯誤彙整。
- 完整承接 1.0.4 的啟動 Port、BuildTools cache、線上模組包結果數、遠端入口與視窗尺寸修正。

## 1.0.4 — 啟動 Port、BuildTools 與桌面操作修正

- 正式 Windows Service 的所有啟動路徑統一在真正啟動前配置 Port：從 `25565` 起選擇最低可用 TCP Listener Port、忽略 UDP-only 占用，並以 session-bound reservation 避免同時啟動重複選號；停止後可立即回收 `25565`。
- Port、`server.properties`／Velocity `--port` 與 registry 會更新為相同的選定值；手動啟動、重新啟動、自動重啟與 desired-run 復原共用相同流程。啟動失敗、取消與終止會釋放 reservation，路徑在 Core directory lease 內重新驗證且拒絕 junction／reparse。BungeeCord／Waterfall 在安全的原子 YAML 編輯器完成前明確 fail closed，不會誤寫 `server.properties`。
- Spigot BuildTools 加入持久、受鎖定的官方 Git bare mirror source cache；後續建置缺少 commit 時先從固定官方 remote 安全 fetch，只有驗證或更新失敗才隔離重建。每次 operation 仍使用 `--no-hardlinks` 獨立 clone，拒絕 hooks、alternates、reparse 與非官方 origin；Maven 工作目錄維持單次作業隔離。
- 線上模組包結果數可選 `20／40／60／100`，切換來源、版本、Loader、分類、排序或結果數會取消舊要求並立即重新載入。CurseForge 依官方每頁限制分頁，結果去重且硬性限制在使用者選擇的數量內。
- 主工具列將「手機遠端」與「Web 控制台」合併為單一「遠端管理」入口；遠端設定內仍可直接開啟 Web 控制台，舊命令別名保留相容性。
- 一般視窗拖曳尺寸會延遲合併後保存，程式關閉前完成最後寫入；最小化／最大化不覆蓋正常尺寸，重新開啟依保存值及目前螢幕工作區安全夾限。修正多螢幕尺寸預先被主螢幕截斷及重疊保存失敗覆蓋新值的競態。

## 1.0.3 — Service 管理與可靠性正式修正

- Service IPC API 1.5 提供受控的伺服器資料夾開啟與永久刪除；真實路徑與刪除能力只允許本機 ACL Named Pipe 使用，不會暴露給 Web／REST，也不再由 GUI 越權直接操作 Service 資料。
- GUI、Web／手機端新增 Service 驗證的有界模組／插件及 Java Runtime 唯讀資訊；Web 每次要求都重新檢查精確 Server scope，且不回傳 Service 實體路徑。
- Service readiness 現在同時要求 HTTP 基礎服務與 Named Pipe IPC 成功綁定。
- 自動更新加入 durable pending、terminal receipt 與崩潰／斷電可重入恢復；只有終態 receipt 完成 ACK 後才清理 pending。
- 新增有界更新保留管理：預設只保留兩個未受保護的舊版本、套件及驗證快取；active、執行中、journal 前後版本、pending 與目前 stable／beta 候選一律保護，清理採 lock、tombstone 及 no-follow 邊界。
- 安裝器升級可原子替換已簽署 stable launcher，並完整快照及還原既有 SCM path、顯示名稱、描述、啟動模式、帳號、失敗動作、SDDL、SID、執行狀態及 Port。
- 通知內容支援繁中／英文；FTB 日期正規化避免顯示 1970 年。
- 舊版 Service 不支援 API 1.5 時，新 GUI 會安全停用相關功能；必須以系統管理員執行 1.0.3 安裝器一併升級 Service，不以單獨替換 GUI 降級繞過。

## 1.0.2 — Service 資料管理修正

- 修正正式 Windows Service 模式把「開啟資料夾」、「完全刪除 Server」、「模組／插件」及「Java Runtime」錯誤停用的問題。
- 新增只允許本機受 ACL 保護 Named Pipe 使用的 `server.directory` 與 `server.delete` API 1.5；真實路徑及永久刪除不會暴露給 Web／REST。
- 永久刪除會封鎖自動重啟、清除執行意圖、安全停止 Java、驗證 Service-owned 目錄與檔案身分，以 no-follow 方式刪除完成後才移除 registry；失敗時保留管理紀錄供重試。
- 模組／插件頁使用 Service 驗證的實體目錄唯讀掃描既有 `mods`／`plugins`；切換 Server 或頁籤會取消舊掃描，不會在投影假路徑建立資料夾或覆寫 Service 設定。
- Java Runtime 頁在 Service 模式可查看註冊的 Java 版本與路徑；本機下載／指定仍保持停用，避免安裝到 Service 不會採用的使用者目錄。
- 安裝／升級只對 `servers`、`runtimes` 給管理員可繼承唯讀權限，資料根維持 traverse-only，`secrets` 與其他 Service 資料不會因此開放。
- 將 hostile Provider 測試 fixture 納入正式 Solution configuration，確保協議升版後 Release 測試不會誤用舊的 Debug／Release 成品。

## 1.0.0 — 正式 Service 產品架構

- 將產品改為 Windows Service 唯一寫入者：Minecraft 程序、Port、Console journal、玩家 presence、備份、模組包更新、遠端 Web、通知、Provider 與產品更新不再由 GUI 生命週期持有；GUI 僅透過具 ACL 的 Named Pipe IPC v1 操作。
- 新增版本化 Windows Service 安裝、升級、健康檢查與回復；使用 `NT SERVICE\MuhunMCSV` 最小權限虛擬帳號、ProgramData ACL、desired-run recovery 及版本化 active pointer，不以 LocalSystem 或互動桌面執行。
- 完成 responsive Web／PWA、手機瀏覽器、iOS 主畫面 App 與簽署 Android APK shell；所有 Web mutation 均由 Service 重新驗證登入、RBAC、Server scope、Origin、CSRF 與冪等鍵，離線狀態不排隊控制命令。
- 完成多帳號、角色、全域與逐 Server 權限、記住裝置、安全戳記、最後 Owner 防護及 SQLite audit；密碼／PIN 以安全 KDF 驗證，秘密與外部 token 只保存於 DPAPI Vault。
- 正式遠端入口使用 Service 管理的 Tailscale Funnel 前景 session；Web Host 只綁 loopback。保留 Cloudflare Named／Quick Tunnel 相容模式，但不開 `0.0.0.0`、UPnP 或路由器 Port Forward，也不承諾第三方網址或免費方案永久不變。
- 新增版本化通知事件、SQLite outbox、去重、節流、重試與歷史；Discord Webhook 限制官方 HTTPS host、redirect、timeout、payload 與 429／5xx／永久錯誤策略，Webhook 不進入 API、事件、audit 或 log。
- 線上模組包目錄整合 FTB、Modrinth 與使用者臨時輸入 API Key 的 CurseForge，提供來源、排序、遊戲版本、Loader、分類、版本、預覽圖與一致缺圖降級；安裝完成後保存來源身分與清單圖示。
- 模組包疊代更新由 Service 重新驗證候選與逐檔 manifest，建立不覆蓋回復點、保留世界／玩家資料、排除可重新取得的核心，並以 journal、啟動健康檢查及自動 rollback 處理失敗或中斷。
- 新增 stable／beta 產品更新：固定 HTTPS host、公鑰 pin、RSA-PSS manifest、ZIP 與逐檔 SHA-256、安全解壓、一次性啟用要求、外部 Updater A/B 切換及 GUI／Service 健康失敗回復。
- 完成 `zh-TW`／`en-US` 版本化 catalog 與即時切換；WPF 與 Web 使用穩定 key／arguments，不依翻譯字串判斷流程。
- 新增簽署 `.mcsvp` Provider 架構、Publisher trust、ECDSA P-256 逐檔簽章、獨立 Provider Host RPC、能力與網路 host allowlist、Windows Job 與有界資源隔離。
- 統一正式深色／黑金外觀、深色 PasswordBox 與訊息框、即時字體／視窗大小預覽、每 Server 背景與 ICON；原生 HWND 深色 surface 避免首幀、resize 或 UI 暫忙時露出白底。
- 保留並擴充高頻效能界線：Console 100 ms 批次與有界 queue、玩家 lazy-load／取消與 presence 合併、資源 latest-only、背景工作成功紀錄自動清除；不使用無玩家自動休眠／連線喚醒，避免模組服反覆重啟負擔。
- 正式發行管線採 locked restore、warnings-as-errors、十個測試專案、self-contained win-x64、Authenticode、RSA-PSS、Provider ECDSA、APK v2/v3/v4（依工具支援）、逐檔 SHA-256、低階磁碟重驗及版本化安裝回復。自簽憑證只提供本機專案金鑰連續性，不等於公開 CA 或 SmartScreen 信譽。

## 0.5.0-preview.9 — 背景工作成功紀錄自動清除

- 線上模組包安裝、核心建立等背景工作成功後，先保留 3 秒完成狀態，再自動從工作中心移除；主畫面摘要、活動文字、總進度、完成數與命令狀態同步更新。
- 失敗與取消的紀錄維持可見，讓使用者查看錯誤或取消原因；既有「清除已結束」仍可手動清除所有終止項目。
- 自動清除不占用固定下載 worker，並由 Coordinator 的生命週期取消權杖管理。到期時會在 UI Dispatcher 重新核對工作 ID 與 `Completed` 狀態；手動先清除、同時完成多項工作及程式關閉都不會造成雙重移除、晚到 UI 回呼或延遲結束。
- 工作中心加入清楚說明：成功紀錄 3 秒後自動清除，失敗／取消紀錄保留。
- App、Core 與 Remote 的 Version／InformationalVersion 提升為 `0.5.0-preview.9`，FileVersion 與 manifest 提升為 `0.5.0.9`，AssemblyVersion 保持 `0.5.0.0`。Preview 8 與所有既有成品、設定、遠端連線及 Server 資料不會被覆寫。

## 0.5.0-preview.8 — Tailscale Funnel 固定公開網址與 Named Tunnel 信任鏈

- 新增 Tailscale Funnel 模式：MCSV 以 foreground `tailscale funnel --yes --https=443 http://127.0.0.1:<port>` 管理固定 `*.ts.net` 公開 HTTPS 網址；不使用 `--bg`，不寫入持久代理規則。手機不需安裝 Tailscale，也不需要固定公網 IP、DDNS、路由器 Port Forward 或自有網域。
- Funnel 共用既有嚴格路由所有權引擎：啟動前拒絕既存或不完整 443 設定，啟動後必須同時確認 `AllowFunnel=true`、精確 root proxy、前景 session 與本次輸出 Origin；停止／Dispose 會終止受 Windows Kill-on-close Job 管理的 child，並在確認路由消失後才釋放 Kestrel。前景 child 意外退出或路由移除未獲確認時，既有 listener 立即切成 503 deny-all guard；只有暫時 backend／network 故障會採有界自動恢復，正常停止與舊 child 延遲事件不會誤觸發。
- Funnel 視為公開網際網路入口，只使用本機註冊帳號與逐帳號權限；忽略 Tailscale／Cloudflare／代理身分標頭，使用全域公開流量限流，固定 Origin 僅接受 HTTPS 443 的合法 `*.ts.net` 名稱。固定網址支援可撤銷裝置登入。
- 遠端設定新增 Funnel 模式、首次 HTTPS／Funnel 核准指引、公開 beta／憑證透明度警示及官方說明連結；完成設定後可隨 MCSV 自動連線，外部服務短暫中斷採有界恢復，MCSV 關閉時所有遠端控制一併停止。
- 新增 Cloudflare Named Tunnel 固定網址模式。Tunnel Token 只經 DPAPI CurrentUser Vault 保存並透過 child process environment 傳遞，命令列、`manager.json`、UI 回填及有界日誌均不含 Token；固定 Origin 不接受 Quick Tunnel、`*.ts.net`、localhost 或帶 path/query/fragment 的網址。
- `remote-security.dat` 提升為 schema 7；schema 6 升級前保留不可覆寫的 DPAPI 密文回復檔。cloudflared 安全下載完成後保存 DPAPI 保護的安裝收據（release tag、固定官方資產身分、大小、SHA-256 與 UTC 時間）。Named Tunnel 在把 Token 交給子程序前只接受 MCSV 管理路徑，拒絕 reparse point，並以同一鎖定串流重新核對大小與 SHA-256；檔案 lease 會維持到受控子程序完成啟動，關閉驗證與執行之間的替換空窗。收據缺失、損毀或檔案變更皆 fail closed。Quick Tunnel 的既有明確路徑行為不變。
- 手機 PWA 新增 1–30 秒有界指數退避自動恢復；不佇列離線 mutation，也不在重試間改寫已接受操作的 Idempotency-Key。Quick Tunnel 仍不提供跨 Origin 保持登入。
- App、Core 與 Remote 的 Version／InformationalVersion 提升為 `0.5.0-preview.8`，FileVersion 與 manifest 提升為 `0.5.0.8`，AssemblyVersion 保持 `0.5.0.0`。Preview 7 與所有既有成品、設定及 Server 資料不會被覆寫。

## 0.5.0-preview.7 — iOS 主畫面 App 與可撤銷裝置登入

- 手機 Web 新增 PWA manifest、iPhone／iPad 主畫面圖示、Apple standalone metadata、safe-area 與 `100dvh` 行動版配置；使用 Safari「加入主畫面」後會以獨立 App 視窗開啟，不需要 App Store、IPA 或描述檔。
- 新增安全且可撤銷的「在這台裝置保持登入」：裝置憑證只透過 `Secure`、`HttpOnly`、`SameSite=Strict` Cookie 傳遞，網頁腳本只保存非秘密的冪等 request ID。Token 每次恢復登入都輪替，重播會撤銷該裝置。
- `remote-security.dat` schema 提升為 5；裝置資料使用 DPAPI Vault 內 master key、每裝置 salt、generation 與 HMAC-SHA256 驗證，不保存已發出的原始 Token。schema 4 升級前保留 byte-for-byte 回復備份，失敗維持 fail-closed。
- 裝置授權閒置 90 天、絕對期限 365 天；每帳號最多 8 台、全域最多 64 台。重設 PIN 或刪除帳號會原子撤銷該帳號裝置，權限更新會撤銷短期工作階段並在下次恢復套用最新權限。
- 電腦端「手機遠端」設定新增記住裝置清單、單一撤銷、重新整理與「登出所有手機」；一般登出會撤銷目前裝置 Cookie 及伺服器端紀錄。
- Service Worker 只快取無腳本、無表單、無 API 的離線說明與圖示；控制頁、`app.js`、API、登入與 Server 資料都不快取，也不排隊任何離線控制操作。
- 固定的 Tailscale 網址可保留已安裝 App 與裝置登入；Cloudflare Quick Tunnel 因網址改變與網站 Origin 隔離，不發出持久裝置 Cookie 並在手機端隱藏保持登入，避免累積無法再使用的裝置名額。正式固定 Cloudflare 網址需後續 Named Tunnel／自有網域。
- App、Core 與 Remote 的 Version／InformationalVersion 提升為 `0.5.0-preview.7`，FileVersion 與 manifest 提升為 `0.5.0.7`，AssemblyVersion 保持 `0.5.0.0`。Preview 6 與所有既有成品、設定及 Server 資料不會被覆寫。

## 0.5.0-preview.6 — 手機 Web 隨 MCSV 自動連線與退出清理

- 已完成遠端設定時，MCSV 啟動後會自動建立 Web Host 與所選 Tunnel；舊設定中保存為停用的遠端會遷移為自動啟動。設定未完成或遠端啟動失敗採 fail-soft，不阻止主視窗與本機 Server 管理功能開啟。
- 「關閉 Web」改為只停止目前這次 MCSV 執行，不再持久化為停用；同一次執行可按重新連線恢復，下次重新開啟 MCSV 仍會自動連線。真正關閉 MCSV、初始化失敗、Dispose 與系統退出都共用冪等、有界的遠端清理。
- 所有遠端視窗共用同一個「本次已關閉」狀態；一般儲存、建立帳號、重設 PIN 或修改權限不會意外重開 Web，只有明確按重新連線／套用並重新連線才解除。設定視窗的遠端操作皆綁定 MCSV 關閉 token。
- 安全關閉與資源釋放會合併同時要求；若第一次因暫時性檔案／資源錯誤失敗，修正原因後可再次關閉，不會被已失敗的快取工作永久卡住。
- 協調器為維護或重新配置而停止服務時，不再誤記成使用者手動「關閉 Web」；只有兩個介面的明確關閉動作會阻止本次執行自動恢復。
- Cloudflare Quick Tunnel 在已進入 Running 後意外失效時，會立即撤銷舊登入工作階段、清除已失效網址並標記可自動恢復；啟動提交前重新核對 Tunnel snapshot，避免 Running／Faulted 競態發布不存在的網址。舊 Tunnel 的延遲事件不會污染新連線。
- 遠端設定與小控制台移除永久啟用勾選語意，統一顯示「關閉 Web」及「重新連線」；介面清楚提示 Web 隨 MCSV 存活，關閉 MCSV 會同時關閉所有遠端控制。
- `manager.json` schema 提升為 9；App、Core 與 Remote 的 Version／InformationalVersion 提升為 `0.5.0-preview.6`，FileVersion 與 manifest 提升為 `0.5.0.6`，AssemblyVersion 保持 `0.5.0.0`。Preview 5 與所有既有成品、設定及 Server 資料不會被覆寫。

## 0.5.0-preview.5 — 啟動順序 Port 配置與 25565 即時重用

- 修正儲存 Instance 設定時就檢查占用、顯示「原 Port 25565 無法使用」並提前改寫為 25566 的行為；儲存 `server.properties`、建立、安裝、匯入及復原也不再執行啟動用 Port 分配。
- 每次真正啟動都從 `25565` 開始掃描當下最低可用 TCP Port，先同步 `server-port=`、畫面與持久設定，再啟動 Java。舊檔即使留在 `25566`，只要 `25565` 已釋放，下次啟動就會回收 `25565`。
- 系統占用快照不再把一般 TCP connection 或停服後的 `TIME_WAIT` 當成 Listener；Minecraft 主 `server-port` 不再被 UDP-only 同號端點阻擋。外部真正 TCP Listener 仍會被保護。
- MCSV 內部的 pending reservation 不再於 Process.Start 回傳後立即清除；Running 時先轉成 ActivePort 再移除，啟動失敗、取消或 terminal state 才釋放，關閉了連續／並行啟動的重複配置空窗。
- 新增真實 loopback Listener／connected-port 分類測試及啟動生命週期契約；建立流程測試不再依賴測試機當下 OS Port 狀態。
- App、Core 與 Remote 的 Version／InformationalVersion 提升為 `0.5.0-preview.5`，FileVersion 與 manifest 提升為 `0.5.0.5`；AssemblyVersion 保持 `0.5.0.0`。Preview 4 及所有既有成品與使用者 Server 資料不會被覆寫。

## 0.5.0-preview.4 — 勾選式批次控制與可安全顯示的遠端 PIN

- 主畫面改為三個明確按鈕「選取／全部啟動／全部關閉」。按下「選取」後，左側每個 Server 圖示前才顯示核取方框；至少勾選一台後兩個批次按鈕才會啟用，離開選取模式會清除勾選且不保留隱藏選取狀態。
- 批次命令只操作執行當下已勾選的 Server，不改變目前詳細頁所選 Server。啟動採逐台處理以避免多個大型模組服同時冷啟動；停止會同時送出安全停止要求，並逐台隔離錯誤、回報成功／略過／失敗摘要。
- 批次執行期間會鎖住核取方框、模式切換、移除與刪除；程式關閉時先取消並等待批次工作，再協調停止所有執行中的 Server，避免關閉後仍啟動下一台或產生失去管理的 Java 程序。
- 遠端安全檔升級為 schema 4。登入驗證仍使用每帳號 PBKDF2 verifier；另將可顯示 PIN 以帳號名稱綁定的個別 Windows DPAPI CurrentUser 密文保存，再由整份 DPAPI vault 提供第二層保護。PIN 不會加入 Web DTO、API 回應、日誌或一般帳號模型。
- 已註冊帳號列改為 `密碼：••••••••` 與眼睛按鈕；點一下顯示、再點一下遮蔽。新增／重設及 SMTP 密碼欄也改為點擊切換。切換模式、重建清單、視窗失焦／關閉或物件釋放時都會立即清除已顯示明文，且同一時間最多顯示一個帳號。
- Preview 3 以前的帳號仍可照常登入，但舊 vault 本來沒有保存可還原 PIN，因此眼睛會停用並提示重設一次後即可顯示；介面不再使用「只能重設」字樣。schema 3 遷移前會另存一次性、byte-for-byte 的 DPAPI 密文回復檔，遷移失敗則保留原檔並 fail-closed。
- App、Core 與 Remote 的 Version／InformationalVersion 提升為 `0.5.0-preview.4`，FileVersion 與 manifest 提升為 `0.5.0.4`；AssemblyVersion 保持 `0.5.0.0`。Preview 3、Preview 2、Preview 1、0.4.10、0.4.9 與使用者 Server 資料不會被覆寫。

## 0.5.0-preview.3 — 多管理員帳號、建立帳號閃退修正與選取操作

- 修正 Cloudflare 建立帳號成功後，遠端帳號變更事件把帶選用參數的方法當成 WPF Dispatcher 委派，因零參數呼叫而拋出 `TargetParameterCountException` 並終止程式；改用明確零參數委派並加入真 STA 建立帳號／事件／Dispatcher 回歸。
- 遠端安全檔升級為有界多帳號 schema；每個帳號分別保存 PBKDF2 verifier、鎖定狀態與六項權限。舊 schema 以原子方式遷移，並先保存一次性、仍為 DPAPI 密文的原始回復檔；權限、PIN 或帳號異動撤銷既有工作階段。
- 電腦端改為「已註冊／帳號清單」，每列提供「設定」、六項權限、重設 PIN 與刪除；建立及重設密碼欄統一為深色並可按住眼睛暫時顯示。原 PIN 從未明文保存，因此清單只顯示遮罩並允許重設。
- Cloudflare Quick Tunnel 只接受沒有 Gmail 綁定的本機帳號，介面隱藏並清除 Gmail 寄信、驗證碼與驗證狀態；Tailscale 只接受與目前 Tailnet Gmail 完全相符的帳號。未知與不適用帳號使用一致的登入失敗邊界，避免跨入口擴權與帳號枚舉。
- 新增 `--remote-account-smoke-test` 發佈診斷：以實際 WPF 對話框建立 Quick 帳號、等待 Dispatcher 更新帳號列並驗證遮罩內容，直接覆蓋這次曾造成閃退的使用者路徑；診斷資料只建立在隔離副本並於封裝前丟棄。
- 分辨率 preset／自訂寬高會立即真正改變主視窗大小，並受目前螢幕工作區限制；未保存關閉改為固定小型深色提示，只提供「取消／保存」，取消會還原暫時尺寸、字體與主題。
- 移除主畫面的「雙控制台」、「全部啟動」與「全部停止」；新增「啟動選取／停止選取」，只操作左側目前選取的 Server，並依執行狀態及更新工作即時停用不適用動作。
- App、Core 與 Remote 的 Version／InformationalVersion 提升為 `0.5.0-preview.3`，FileVersion 與 manifest 提升為 `0.5.0.3`；AssemblyVersion 保持 `0.5.0.0`。Preview 2、Preview 1、0.4.10、0.4.9 與使用者 Server 資料不會被覆寫。

## 0.5.0-preview.2 — 即時設定預覽、遠端權限與核心清單加速

- 視窗尺寸、自訂寬高與字體大小改為主視窗即時預覽；未套用便關閉時可選保存套用或不保存關閉，後者完整還原暫時主題、字體與尺寸。設定頁底部統一為「關閉／套用」。
- 全域設定移除 Auto／Manual 記憶體模式，只保留一組全域預設 Min／Max；每台 Server 仍可選全域預設、自動或手動，點選自動會以可取消、有界、非遞迴的頂層模組統計立即更新滑桿與數值。
- Cloudflare Quick Tunnel 改為電腦端直接建立本機帳號，不要求 Gmail；新增啟動、停止、重新啟動、指令、玩家管理與備份六項權限，手機 UI 與後端端點皆強制執行。Tailscale 模式保留 Gmail／Tailnet 身分邊界。
- 已啟用且設定完整的遠端 Web 隨 MCSV 自動啟動；小控制台移除手動啟動動作，保留重新連線／重新啟動與關閉 Web。MCSV 結束時仍停止 Web 與 owned cloudflared。
- 建立核心視窗改為立即顯示可信產品與版本化本機快取，官方版本在背景有界並行、分批刷新；快取只作瀏覽提示，實際安裝仍重新 canonical resolve 並維持來源、大小、雜湊與簽章驗證。線上模組包流程未修改。
- `0.5.0-preview.2` 使用獨立版本識別與新成品資料夾，不覆寫 Preview 1、0.4.11 Remote Preview 4、0.4.10、0.4.9 或使用者 Server 資料；仍不包含無玩家休眠／連線喚醒。

## 0.5.0-preview.1 — 安全模組包疊代更新、集中設定與免 Tailscale Web 測試版

- 新增 FTB／Modrinth 模組包安全疊代更新入口：只在 Server 停止且能確認既有來源身分時選擇較新版本，保留世界、玩家與管理資料，並以受控交易切換安裝器所管理的模組包內容。這是 Preview 驗證功能，尚不宣稱所有第三方模組組合皆具正式版穩定性或跨版本相容性。
- 每次疊代更新在檔案切換前建立獨立、不覆蓋舊檔的資料備份，涵蓋 `mods`／`plugins`、設定、腳本、世界／地圖、玩家與管理清單等可回復內容；可重新下載或重建的 Server 核心、libraries、versions、runtime、logs、cache 與既有 backups 不納入該備份。
- Server 背景與清單 ICON 從主畫面「外觀」頁籤移至左側 Server 清單右鍵設定。背景可調整 `0–100%` 透明度；ICON 不提供透明度選項並固定以 `100%` 顯示。
- 右上角齒輪改為集中設定視窗尺寸、字體大小、四種魂系深色主題（含黑金），以及往後新增／匯入 Server 使用的預設值。每台 Server 的 Java 記憶體可使用管理器預設（介面中的「不指定」）、依模組包規模自動估算或手動指定；拖動配置值會採手動模式。
- 手機 Web 遠端新增 Cloudflare Quick Tunnel 模式，不依賴 Tailscale，手機與電腦只需各自能連上網路。隨機 `trycloudflare.com` 網址不是授權憑證；遠端操作仍必須通過電腦端 Gmail 驗證後建立的固定本機帳號與數字 PIN，既有登入限流、工作階段及請求保護繼續生效。
- 新增一鍵取得 `cloudflared`：只接受 Cloudflare 官方 GitHub Release 的 Windows x64 資產，核對資產名稱、大小、Release SHA-256 digest 與下載內容，經受控 redirect、暫存及原子替換後才可使用；失敗會保留原有檔案並回報原因。
- MCSV 內可開啟小型 Web 控制台查看遠端服務與 Tunnel 狀態；Web Host 與 `cloudflared` 由 MCSV 管理，MCSV 關閉時一併停止，不安裝獨立常駐服務。Quick Tunnel 是 Cloudflare 的測試／開發入口，網址會變動且不視為正式 SLA 服務。
- 本版沒有加入無玩家休眠／停止、遊戲 Port 代理或玩家登入喚醒 Server；模組服不會因本功能被反覆關閉與重啟。
- `0.5.0-preview.1` 使用獨立版本識別與成品位置，不覆寫 `0.4.11 Remote Preview 4`、`0.4.10`、`0.4.9` 或其他既有版本；所有新功能均以測試版交付，不沿用舊版正式穩定性聲明。

## 0.4.11 Remote Preview 4 — 電腦端 Gmail 驗證與持久遠端帳號

- 移除手機端單次配對連結與註冊入口；遠端帳號只能在電腦端建立。手機網站只接受已核准的 6–32 位帳號與 4–12 位數字密碼，程式、網路或電腦重新啟動後可用原帳號重新登入，不需要再次產生配對碼。
- 電腦端新增 Gmail SMTP + TLS 設定及六位驗證碼流程。寄件固定使用 `smtp.gmail.com:587` 與必要的 STARTTLS、正常平台憑證驗證及 Google 16 位應用程式密碼；不接受 Gmail 一般密碼，不讀取郵件，也不在手機端登入 Google。
- SMTP 應用程式密碼、核准 Gmail、帳號 verifier 與登入鎖定狀態獨立寫入 `remote-security.dat`，整份檔案由 Windows DPAPI CurrentUser 加密；數字密碼使用每帳號 32-byte salt 與 PBKDF2-HMAC-SHA256 600,000 次，不寫入 `manager.json` 或明文檔案。
- Gmail 驗證碼為密碼學隨機六位數、10 分鐘、單次使用；重寄會先撤銷舊碼，限制每分鐘一次、每小時五封及連續錯誤五次鎖定 15 分鐘。遠端登入另有每分鐘限流、每五次錯誤的遞增持久鎖定，以及單一高成本 KDF 併發上限，避免登入攻擊拖慢 Minecraft Server。
- 登入工作階段沿用精確 Tailscale Gmail、loopback-only Kestrel、私人 foreground Serve、Secure／HttpOnly／SameSite=Strict Cookie、CSRF、精確 Origin、Idempotency-Key 與有界操作。停止服務或桌面撤銷會登出所有手機，但不刪除核准帳號。
- 手機端支援伺服器 `Retry-After`，登入鎖定期間顯示可重試時間並停用按鈕。錯誤帳號與錯誤 PIN 共用持久失敗計數；非核准 Tailnet 身分的全域流量配額與核准管理者隔離，Kestrel 另限制同時連線數。
- 同一可攜式資料夾新增跨 Windows／RDP 工作階段的獨占鎖檔，避免兩個背景實例持有過期 vault 而讓刪除／撤銷失效。安全檔改為先做 128 KiB 有界長度檢查再配置與 DPAPI 解密，損毀時維持 fail-closed。
- 電腦端新增「移除寄件設定」，可在完成註冊後只刪除 Gmail App Password、保留核准帳號；使用說明醒目建議採用專用寄件 Gmail，並說明 DPAPI 無法隔離同一 Windows 使用者下的惡意程式。
- 只有 Tailscale 已安裝但後端暫時未就緒時才以有界退避在背景恢復；HTTPS 授權、Serve 衝突、Gmail、DPAPI 與身分設定錯誤不會永久重試。
- `remote-security.dat` 及原子寫入暫存檔已排除版本控制與同根目錄 Server 備份；runtime/source 發佈採明確白名單。新增 MailKit 4.17.0 並附 `THIRD-PARTY-NOTICES.txt`；套件弱點掃描未回報已知弱點。
- App、Core 與 Remote 的 Version／InformationalVersion 提升為 `0.4.11-remote-preview.4`，FileVersion 與 manifest 提升為 `0.4.11.4`；AssemblyVersion 保持 `0.4.11.0`。Preview 3／2／1 與 0.4.10／0.4.9 成品不會被覆寫。

## 0.4.11 Remote Preview 3 — Tailscale HTTPS 首次授權修正

- 修正 Tailnet 尚未啟用 HTTPS Certificates 時，程式把 Tailscale 的一次性授權需求隱藏在背景，最後只顯示「未在預期時間內建立 HTTPS 8443」的誤導性逾時。
- 啟用手機遠端前會從 `tailscale status --json` 嚴格確認 `CertDomains` 包含目前裝置的 MagicDNS 名稱；未啟用、空值、只含其他舊網域或欄位格式異常都會在建立本機 Web Host 與 foreground Serve 前安全停止。
- 只有需要授權時，遠端設定視窗才顯示「開啟 Tailscale HTTPS 設定」按鈕，固定開啟 Tailscale 官方 DNS 管理頁；完成一次性 Enable HTTPS 後可回到程式重新套用。
- 不解析或自動開啟 Tailscale control-plane 回傳的動態 URL，避免自訂 control server 或未來 CLI 格式變動擴大外部導向邊界。
- App、Core 與 Remote 的 Version／InformationalVersion 提升為 `0.4.11-remote-preview.3`，FileVersion 與 manifest 提升為 `0.4.11.3`；AssemblyVersion 保持 `0.4.11.0`。Preview 2、Preview 1 與 0.4.10／0.4.9 成品不會被覆寫。

## 0.4.11 Remote Preview 2 — 手機遠端對話框閃退修正

- 0.4.11 Remote Preview 1 在正式現場按下「手機遠端」時，WPF 將 `Run.Text` 的預設 TwoWay binding 套到 `RemoteAccessSettingsViewModel.TailscaleStatusText` 唯讀屬性，拋出未處理 `System.InvalidOperationException` 並終止 GUI；Preview 1 因此撤回，不應繼續使用。
- 遠端對話框的 `TailscaleStatusText`、`RemoteServiceStatusText`、`PublicUrl` 與 `PairingCode` 唯讀顯示 binding 均明確設為 `Mode=OneWay`，避免修正第一處後由下一個相同 binding 再次閃退。
- 新增 `--remote-dialog-smoke-test` 診斷入口，實際建立遠端對話框、完成 WPF layout 並關閉；測試不啟用 Tailscale Serve、遠端 Host 或網路操作。
- App、Core 與 Remote 的 Version／InformationalVersion 提升為 `0.4.11-remote-preview.2`，FileVersion 與 manifest 提升為 `0.4.11.2`；AssemblyVersion 保持 `0.4.11.0`。主視窗版本、正式檔名及三組 production Provider User-Agent 同步更新為 Preview 2。
- 同目錄可攜式備份同時排除 `Muhun MCSV Manager 0.4.11 Remote Preview 2.exe` 與已撤回的 `Muhun MCSV Manager 0.4.11 Remote Preview 1.exe`。0.4.10 穩定版來源與成品不受影響。

## 0.4.11 Remote Preview 1 — 手機跨網路遠端控制測試版（已撤回）

- 由 0.4.10 穩定版建立獨立預覽分支；0.4.10 成品與來源不受影響。
- 新增只監聽 `127.0.0.1` 的 ASP.NET Core 手機管理頁，透過 Tailscale Serve 私人 HTTPS 在不同 Wi-Fi／5G 網路間使用，不開啟路由器 Port、不使用 UPnP，也不啟用公開 Funnel。
- 以 Tailscale 提供的 `Tailscale-User-Login` 精確比對指定 Gmail，再要求 256-bit、五分鐘、單次使用的桌面配對連結；工作階段使用 Secure／HttpOnly／SameSite=Strict Cookie，所有 mutation 另驗 CSRF 與精確 Origin。
- Serve 改由本程序持有 foreground 工作階段並置入 Windows Kill-on-close Job Object；先確認無既存 8443 設定、綁定 loopback Host、再次確認衝突後才分享，關閉時先撤銷身分與配對、移除通道，最後才釋放本機 Port。
- Server mutation 使用有界、工作階段範圍的 Idempotency-Key 快取；手機斷線、停用手機網頁或重新套用遠端設定不會取消已接受的重啟／備份，安全重送也不會重複執行。只有整個桌面程式關閉時才取消尚未完成的遠端操作，Host shutdown 會以固定上限 drain 已脫離 HTTP request 的工作。遠端備份在同 Server 已有備份／恢復點時直接回報忙碌，不建立長佇列。
- 手機頁第一階段提供總覽、伺服器狀態、CPU／記憶體／玩家、有界一般與錯誤警告控制台、啟動／安全停止／重啟、單行 Minecraft 指令、玩家 kick／ban／pardon／op／deop／whitelist 操作及建立 ZIP 備份。
- 遠端資料採限制筆數的快照與節流輪詢，頁面隱藏時暫停；不重新掃描完整控制台或對每一行觸發 WPF 更新，避免重現先前整台電腦延遲。
- 高風險的永久刪除、任意 JAR／模組／外掛上傳、匯入、任意 Windows 路徑與 Java executable 選擇尚未遠端化；手機也不提供 Explorer、關閉 Manager 或作業系統 shell。
- 明確不包含無玩家休眠、遊戲 Port 監聽或連線喚醒功能。手機開頁、登入、輪詢或配對都不會自動啟動／停止 Minecraft Server。

## 0.4.10 最小化至系統匣

- App／Core、Assembly、FileVersion、ProductVersion、manifest、主視窗版本及三組 production Provider User-Agent 升為 `0.4.10`／`0.4.10.0`；正式檔名為 `Muhun MCSV Manager 0.4.10.exe`。
- 主視窗最小化時隱藏到 Windows 系統匣。雙擊系統匣圖示或選擇「開啟 MCSV Manager」會還原最小化前的一般／最大化狀態；系統匣「結束」與標題列 `X` 都沿用原有安全 shutdown、背景工作確認與 Server 停止流程，`X` 不改成隱藏。
- 系統匣建立失敗時保留一般 WPF 最小化的 fail-soft 降級已完成；adapter 建立失敗不會讓應用程式初始化失敗，且由真 STA lifecycle 測試覆蓋。
- 除系統匣生命週期外，0.4.9 的正式功能與安全邊界維持不變。0.4.10 明確不包含 `0.5.0-preview.1` 的無玩家休眠、監聽遊戲 Port 或連線喚醒實驗。
- 同目錄可攜式備份新增 `0.4.10` 排除，完整保留 `0.4.9` 至 `0.2.5` 與舊 `MinecraftServerManager.exe` 相容排除，並額外排除獨立實驗成品 `Muhun MCSV Manager 0.5.0-preview.1.exe`，避免把任何管理器 EXE 包入 Server 備份。
- 由 0.4.10 最終凍結來源 fresh 完成 Release build（0 warnings／0 errors）、Core 635／635、App 連續三輪各 223／223、production native `NotifyIcon` 1／1、tray 競態 10 輪各 14／14、中文與空白路徑六入口、self-contained／signed 六入口、Windows 事件窗、Authenticode／CMS、PE、runtime／source 安全封裝與 SHA-256 關卡；不沿用下方 0.4.9 的證據。

## 0.4.9 高頻狀態與控制台 UI 效能

- App／Core、Assembly、FileVersion、ProductVersion、manifest、主視窗版本及三組 production Provider User-Agent 升為 `0.4.9`／`0.4.9.0`；正式檔名為 `Muhun MCSV Manager 0.4.9.exe`。同目錄可攜式備份新增 0.4.9 排除，並完整保留 0.4.8 至 0.2.5 與舊 `MinecraftServerManager.exe` 相容排除。
- 控制台不再為每一行同步更新 WPF collections。每個 Instance 採 100 ms UI cadence、4,096 筆 pending 上限與最新 2,000 行 UI tail，批次完成後只送一次 Reset；大量裁切改用 `RemoveRange`，消除逐行移除導致的 O(n²) 工作。Core 仍持續擷取並保留有界原始紀錄，沒有暫停 stdout／stderr 或丟棄 Core retained log。
- 控制台與「錯誤／警告」面板只在使用者位於尾端時跟隨；向上捲動後新批次不會強制拉回。玩家 presence parser 先以必要 token fast gate 排除不可能是登入／離線事件的一般文字，再進入完整規則；0.4.8 的 strict severity、multiline `DiagnosticId` 與 unknown stderr 分類行為保持不變。
- 已知玩家資料改為第一次進入「玩家管理」分頁時才 lazy 背景讀取；切換分頁、改選 Server 或新重讀請求會取消舊工作，只有最新結果可以提交。「顯示已知玩家」只切換已載入／線上名單的投影，不控制磁碟工作。玩家 presence 採 100 ms 合併、4,096 上限及單次 Reset；資源狀態則採 per-Instance latest-only，密集樣本不再逐筆排入 Dispatcher。
- canonical Server 路徑落在 OneDrive 同步根目錄時，設定頁顯示明確效能警告。管理器不會自動搬移 Server、停止 OneDrive 或降低 Java 優先權；使用者應先停止 Server，再自行搬到不受同步的本機資料夾。本版不限制 JVM／RAM／CPU，也不以降低 Server 工作量作為 UI 優化。
- 全域主題資源只在 WPF Application 所屬 Dispatcher 執行緒套用；背景／headless ViewModel 初始化不再接觸半初始化的 process-global ResourceDictionary。App 測試的唯一 Application 改用不啟動 production composition 的 STA host，且全 assembly 序列化，以封住 `WindowColor` 初始化競態與測試期間誤啟正式視窗／服務的風險。
- 0.4.9 正式凍結來源的 fresh Release build 為 0 warnings／0 errors，Core full 為 635／635，App full 連續三輪各為 207／207；metadata／backup、console、player／resource、presence、OneDrive、Application resource 與真 WPF STA targeted 分別為 4／4、1／1、2／2、17／17、13／13、4／4、69／69、5／5、2／2、37／37。WPF Application 資源競態修正另有 20 輪 fresh 真 STA、每輪 37／37 的補充 soak；framework／signed 中文與空白路徑六項 GUI、簽章、事件窗及安全封裝全部由同一凍結來源 fresh 完成，未沿用 0.4.8 或任何作廢 release root 的證據，詳見 `docs/測試報告-0.4.9.md`。

## 0.4.8 每 Server 錯誤／警告分流

- App／Core、Assembly、FileVersion、ProductVersion、manifest、主視窗版本及三組 production Provider User-Agent 升為 `0.4.8`／`0.4.8.0`；正式檔名為 `Muhun MCSV Manager 0.4.8.exe`。同目錄可攜式備份新增 0.4.8 排除，並完整保留 0.4.7 至 0.2.5 與舊 `MinecraftServerManager.exe` 相容排除。
- 修正把 process stream 誤當 log severity 的呈現方式。stdout／stderr 仍完整保留，但只有明確解析出的 WARN／ERROR／FATAL 才是 diagnostic；INFO 即使由 stderr 傳入也維持一般資訊，未分類 stderr 顯示中性的 `STDERR`，不會自動標成紅色 `ERR`。Forge／NeoForge 26.2 的 Netty／Log4j 非致命 stderr 因此不再整片泛紅。
- 每個 Server 的設定頁新增「將錯誤／警告與控制台分開顯示」。未勾選時「錯誤／警告」tab 完全不出現且控制台維持混合；勾選後才顯示相鄰 tab，並把 strict diagnostics 從普通控制台移出。切換會立即生效及持久化，使用穩定 tab key，所以從設定頁切換或改選 Server 不會跳到錯誤頁籤。
- 每個 Server 只保留一份有界 2,000 行時間順序 history，再即時 reflow 成普通與 diagnostic 兩個檢視；切換選項不遺失歷史順序。多行 stack trace 以共同 `DiagnosticId` 聚合成一個事件，延續行仍完整顯示；雙控制台依各 Server 的 opt-in 狀態安全降級。
- `manager.json` schema 升為 `5`。舊 JSON 缺少 `SeparateDiagnosticOutput` 或其值為 `null` 時 effective false，沿用混合控制台；新建立或匯入的 Server 在首次持久化前預設 true。管理器已知的 timeout、停止與自動重啟失敗等事件會明確指定 severity，不以文字猜測。
- 凍結來源已完成 fresh Release 0 warnings／0 errors、Core 632／632、App 連續三輪各 186／186；metadata 4／4、backup exclusion 1／1、classifier／process／JSON 88／88、diagnostic 16／16 及真 STA／visual／persistence／contract 7／7 亦全數通過。framework／signed 中文＋空白路徑六項 GUI、Authenticode／CMS／PKCS#9、PE、事件窗與封裝關卡均通過；所有 0.4.8 證據都來自本版 fresh 成品，沒有沿用或覆寫 0.4.7。詳見 `docs/測試報告-0.4.8.md`。

## 0.4.7 Forge／NeoForge headless 啟動修正

- App／Core、Assembly、FileVersion、ProductVersion、manifest、主視窗版本及三組 production Provider User-Agent 升為 `0.4.7`／`0.4.7.0`；預定正式檔名為 `Muhun MCSV Manager 0.4.7.exe`。同目錄可攜式備份新增 0.4.7 排除，並完整保留 0.4.6 至 0.2.5 與舊 `MinecraftServerManager.exe` 相容排除。
- 修正 Forge／NeoForge 等專用 Minecraft Server 在偵測結果的持久化 `ServerArguments` 為空時仍建立原生 AWT／Swing「Minecraft server」視窗。`CreateNoWindow` 只影響 Windows console，不能代替 Minecraft application argument `nogui`；0.4.7 因此在最終 launch definition 組合時補上缺少的 `nogui`。
- `nogui` 僅附加在 `-jar <server.jar>` 或所有 JVM `@argument-file` 之後，不會進入 JVM options，不會修改官方 `win_args.txt`／`unix_args.txt`、Installer 產物或 typed provenance，也不重建已存在的 Server。既有任意大小寫的 `nogui` 保留且不重複，持久化 `ServerArguments` 本身不改寫。
- 注入範圍限於專用 Minecraft 核心；Velocity、Unknown 與 Custom JAR 不會被假設支援 `nogui`。實機 Forge 26.2 與 NeoForge 26.2 均到達 `Done`，額外 Minecraft server 視窗數為 0，管理器送出 `stop` 後程序皆以 exit code 0 結束。
- 0.4.7 以 bundled .NET SDK 10.0.400 完成 fresh Release 0 warnings／0 errors、Core 593／593、App 三輪各 170／170、framework／signed 六項 GUI、Forge／NeoForge 26.2 headless 實機、Authenticode／CMS／PKCS#9、Runtime／Source ZIP 及外部 7 項 SHA manifest 驗證；詳見 `docs/測試報告-0.4.7.md`。所有證據均來自本版凍結來源與新成品，未挪用或覆寫 0.4.6 歷史結果。

## 0.4.6 可信核心建立與多工背景工作

- App／Core、Assembly、FileVersion、ProductVersion、manifest、主視窗版本及三組 production Provider User-Agent 升為 `0.4.6`／`0.4.6.0`；正式檔名為 `Muhun MCSV Manager 0.4.6.exe`。同目錄可攜式備份新增 0.4.6 排除，並完整保留 0.4.5 至 0.2.5 與舊 `MinecraftServerManager.exe` 相容排除。
- 修正四條官方 Loader／BuildTools 成功輸出被通用辨識器誤拒的路徑：Spigot／CraftBukkit 使用建置後再次雜湊及結構驗證的 typed provenance 接受新版 bootstrap JAR；Fabric 接受官方 Installer 產生、把 launcher properties 內嵌於 JAR 的啟動器；NeoForge 接受新版官方 direct-main 與精確 FML 版本參數；Forge 的官方 shim 必須逐位等於已驗證 Installer 內嵌項目並通過 manifest／metadata 檢查。
- Spigot／CraftBukkit 目錄擴為 67 個 stable aliases，涵蓋 Minecraft `1.8` 至 `26.2`。其中現代 12 版仍使用上游提供的官方 output SHA-256；舊版 55 筆使用官方不可變四 refs、固定 BuildTools／受管理 MinGit、建置前後 HEAD 重驗、嚴格 JAR 結構檢查及 promotion 前後一致的本機 SHA-256，不把舊版假裝成具有上游 output hash。
- 核心建立與 FTB／Modrinth 模組包安裝改送入背景工作協調器；選擇視窗提交後立即關閉，主 GUI 可繼續操作並再加入多個工作。主視窗底欄顯示整體活動與進度，`☰` 開啟非 modal 工作中心，可檢視、取消與清除工作紀錄。
- 排程器依 CPU／可用記憶體選擇並行度；96 GiB 以上且至少 24 logical processors 的高效能 profile 為全域 10、BuildTools 3。General 與 BuildTools 使用雙 queue 並共用全域 slot，避免大量 heavy 工作餓死一般下載；10,000 次高頻進度回報採 latest-value coalescing，不為每筆更新建立 Dispatcher 工作。
- 同名及相同目標在排隊階段先以 NFKC／case-insensitive identity 保留；最終 Server 註冊共用 registry gate，資料夾以 staging 單次目錄搬移提交，單一 JAR 匯入也先進 GUID staging 再原子 promotion，取消或碰撞只清理明確擁有的暫存內容。Adoptium Runtime 對同一 canonical destination 使用跨 provider instance gate，讓不同目的地保持並行、相同目的地只安裝一次並重驗。
- FTB 固定高吞吐工作數，Modrinth 依檔案數與總大小自動選擇 1／2／4／8／12／16 線；網路與背景工作依實際工作量自動擴縮，不需要手動設定。CurseForge 仍不在正式 UI／production workflow，公開入口只有不需 API Key 的 FTB 與 Modrinth。
- 0.4.6 以 bundled .NET SDK 10.0.400 完成 Release 0 warnings／0 errors、Core 579／579、App 三輪各 170／170、FTB／Modrinth fresh live、Spigot／CraftBukkit 與三種官方 Loader live、framework／signed 六項 GUI、Authenticode／PKCS#9、Runtime／Source ZIP 及外部 7 項 SHA manifest 驗證；詳見 `docs/測試報告-0.4.6.md`。

## 0.4.5 BuildTools JVM 穩定模式與受控 Java 子程序

- App／Core、Assembly、FileVersion、ProductVersion、manifest、主視窗版本與三組 production Provider User-Agent 升為 `0.4.5`／`0.4.5.0`；正式檔名為 `Muhun MCSV Manager 0.4.5.exe`。同目錄可攜式備份新增 0.4.5 排除，並完整保留 0.4.4、0.4.3、0.4.2、0.4.1、0.4.0、0.3.1、0.3.0、0.2.5 與舊 `MinecraftServerManager.exe` 相容排除。
- Spigot／CraftBukkit BuildTools 使用 Java 25 以上時，受控 `_JAVA_OPTIONS` 會加入 `-XX:TieredStopAtLevel=1`，讓 BuildTools 及其 Java／Maven 子程序限制在 C1 編譯層級，避開現場觀察到的 HotSpot JIT compiler replay 致命失敗；Java 24 以下不強制此選項。既有真 LF `line.separator`、受管理 MinGit、固定 refs 與官方 output SHA-256 硬性關卡均維持不變。
- BuildTools 非零結束不再沿用共用 runner 的 `ModLoader Installer` 錯誤標籤，而是拋出專用 `SpigotBuildToolsProcessException`。工作目錄清除前先辨識 replay／`hs_err`／compiler-task 證據，只保留有界的檔名、大小與嚴格 allowlist 遮蔽摘要；未分類的原始輸出、主機細節、路徑、URL、帳密及其他可能的秘密不會進入摘要。
- 診斷擷取不會改變清理責任：取消、程序失敗或 JVM 致命錯誤都必須先收斂子程序，再完整移除未提交的 BuildTools operation 與 `.core-installing-*`。若診斷本身失敗，仍以原始程序錯誤為主並繼續清理；若清理失敗則明確回報，不把半成品加入管理清單。
- Forge／NeoForge／Fabric 官方 Loader Installer 改由共用的最小 Java 環境執行：先清空 ambient 環境，再只加入受管理 Java、必要 Windows 系統工具與 operation-private `HOME`／`USERPROFILE`／`TEMP`／`TMP`。`_JAVA_OPTIONS`、`JAVA_TOOL_OPTIONS`、`JDK_JAVA_OPTIONS` 及 Maven／Gradle／Git 注入變數不會被繼承。
- Loader JVM 另以固定 system properties 鎖定 `user.home`、`java.io.tmpdir` 與 `user.dir`。Fabric 的 `-dir` 以及 Forge／NeoForge 的 `--installServer` 都傳入同一個絕對 operation output 目錄，避免 ambient `user.dir` 或工具設定把檔案寫出 staging；完成後仍須經原有輸出樹與 Loader 身分驗證才可提交。
- `java -version`／`javac -version` probe 使用精確受管理執行檔、其 `bin` 工作目錄與相同最小環境，不讀取使用者 JVM／Maven／Gradle／Git 設定；輸出仍受大小及時間上限約束。
- 0.4.5 以 bundled .NET SDK 10.0.400 完成 Release 0 warnings／0 errors、Core 560／560、App 三輪各 147／147、fresh FTB／Modrinth、Spigot／CraftBukkit 26.2 官方 output SHA、framework／signed 六項 GUI、Authenticode／PKCS#9 與 Runtime／Source ZIP 稽核；所有數字均取自本版新位元，詳見 `docs/測試報告-0.4.5.md`。

## 0.4.4 Spigot 可重現輸出與線上來源收斂

- App／Core、Assembly、FileVersion、ProductVersion、manifest、主視窗版本與三組 production Provider User-Agent 升為 `0.4.4`／`0.4.4.0`；正式檔名為 `Muhun MCSV Manager 0.4.4.exe`。同目錄可攜式備份新增 0.4.4 排除，並保留 0.4.3、0.4.2、0.4.1、0.4.0、0.3.1、0.3.0、0.2.5 與舊 `MinecraftServerManager.exe` 相容排除。
- 線上模組包正式功能收斂為 FTB 與 Modrinth：UI、鍵盤焦點、Automation、診斷 fixture 及 production workflow 四個公開入口都不再暴露 CurseForge，不爬取網站，也不要求 API Key。Core 內的 CurseForge 低階 Provider 僅保留為相容實作與安全契約測試，不是 0.4.4 的 UI 功能或隱藏入口。
- Spigot／CraftBukkit 建立前會 fresh 讀取使用者選擇的 alias JSON 與其不可變數字 `VersionIdentity` JSON；`name`、四個 repository refs、Spigot／CraftBukkit hashes、`toolsVersion` 與原始 `javaVersions` 必須逐欄一致。數字端點不可用或任何欄位漂移都會在進入長時間編譯前 fail closed，BuildTools 的 `--rev` 也固定使用數字 identity，不再依賴可移動 alias。
- Windows BuildTools 使用固定 SHA-256／版本驗證的受管理 MinGit、隔離 PATH／Maven home／operation root，並把私有 global Git config 及預先固定 refs 的 repository 設為 `core.autocrlf=input`；BuildTools 建立的子 repository 也會繼承相同規則。Java／Maven 子程序另收到包含真實 LF 字元的 `line.separator`，避免 Windows CRLF 改寫 Maven POM／patch 輸入。
- 四個官方 repository 在 BuildTools 完成後仍以受管理 Git 驗證實際 HEAD 是否等於 plan refs；最終 Spigot／CraftBukkit JAR 仍必須硬性符合官方逐版 JSON 的 output SHA-256。0.4.4 沒有把官方 hash 降級成提示，也不以「已有 JAR」代替來源與輸出身分驗證。
- 上述 deterministic identity、line-ending 與 post-ref 關卡是針對 Windows 上約 5–7 分鐘編譯後才出現 output SHA-256 假失敗的根因；可在開始 BuildTools 前辨識 metadata 漂移，並使合法本機輸出遵守官方可重現條件，避免再次浪費完整編譯時間。
- 0.4.4 以 bundled .NET SDK 10.0.400 完成 Release 0 warnings／0 errors、Core 548／548、App 147／147（另連跑三輪全綠）、FTB／Modrinth fresh live、Spigot／CraftBukkit 26.2 官方 output SHA 實機 gate、signed self-contained 六項診斷、Authenticode／PKCS#9 時間戳與 Runtime／Source ZIP 稽核；所有數字均取自本版新位元，未沿用 0.4.3。詳見 `docs/測試報告-0.4.4.md`。

## 0.4.3 BuildTools 內嵌進度、完整清理與永久刪除

- App／Core、Assembly、FileVersion、ProductVersion、manifest、視窗版本與三組 Provider User-Agent 升為 `0.4.3`／`0.4.3.0`；預定正式檔名為 `Muhun MCSV Manager 0.4.3.exe`。同目錄可攜式備份新增 0.4.3 排除，並保留 0.4.2、0.4.1、0.4.0、0.3.1、0.3.0、0.2.5 與舊 `MinecraftServerManager.exe` 相容排除。
- 「匯入現有 Server」選擇視窗放大為 700×500、最小 640×470 並允許使用者縮放；資料夾與單一 JAR 選項共用同一按鈕模板、58／自動延伸／72 欄寬及 112 最小高度，修正上下卡片未對齊與 JAR 選項被裁切。
- Spigot／CraftBukkit 建立流程預先配置固定版本、固定 SHA-256 且執行版本驗證的 MinGit ZIP，將受信任的 Git／shell 置於隔離子程序 PATH。BuildTools 因此不再下載或啟動會顯示原生「Please, wait...」視窗的 PortableGit SFX；所有狀態改在核心建立器內顯示。
- 核心建立器新增整體階段與詳細子作業兩行、兩條不可拖曳的內嵌進度。BuildTools／MinGit 輸出只更新詳細列，不會把長時間本機編譯誤顯示成另一個可操作視窗。
- Spigot／CraftBukkit 仍只接受官方 BuildTools 與官方逐版 revision／output SHA-256；建立使用受管理完整 JDK、隔離 Maven home 與本次工作目錄，完成後才驗證並提交目標 Server JAR。
- 安全清理會在確認不是 reparse point 後清除 Git pack 等一般唯讀檔的 ReadOnly 屬性，並只針對 Windows Access／Sharing／Lock／Directory-not-empty 做有界重試。取消或失敗必須收斂外部程序，再清除 `.core-installing-*`、BuildTools operation、MinGit／JDK partial 與 staging；清理失敗不會被靜默當作成功。
- Modrinth 多檔下載改以已驗證檔案數與總大小自動選擇 1／2／4／8／12／16 線，仍受 16 線硬上限、首錯取消、hash 驗證與 staging 原子提交約束；FTB 官方 Installer 維持 16 線。單一大型 artifact 不盲目拆成 Range 多段，避免 CDN／redirect／hash 邊界退化。
- Server 右鍵選單在「從管理清單移除」之外新增「完全刪除 Server」。永久刪除只有「確認刪除／取消」二次確認；確認前即開啟並持有目標 identity lease，確認後再協調停止，最後以 Windows no-follow handles、Volume／File ID 與 handle-based delete disposition 刪除同一物件。磁碟根、個人／管理器危險根與其祖先、Windows／System／Program Files／ProgramData 子樹、裝置／extended-path 語法、reparse／redirecting intermediate，以及與其他受管理 Server 任一方向重疊的路徑均拒絕；只有原目標完整消失後才移除管理記錄。
- 0.4.2 的 Temurin JDK ZIP Unix mode 修正、Windows 原子提交重試、Core／Online 深色 busy theme、移除轉場與 FTB ANSI／ETA 分行均保留。0.4.3 是全新 Release 位元，最終測試數、live smoke、hash、簽章、時間戳與封裝結果不得沿用 0.4.2；詳見 `docs/測試報告-0.4.3.md`。

## 0.4.2 Java ZIP、深色狀態與下載進度 Hotfix

- App／Core、Assembly、FileVersion、ProductVersion、manifest、視窗版本與三組 Provider User-Agent 升為 `0.4.2`／`0.4.2.0`；預定正式檔名為 `Muhun MCSV Manager 0.4.2.exe`。同目錄可攜式備份新增 0.4.2 排除，並保留 0.4.1、0.4.0、0.3.1、0.3.0、0.2.5 與舊 `MinecraftServerManager.exe` 相容排除。
- 修正 Adoptium Temurin 16／17／21／25 JDK ZIP 的 Unix mode 一般目錄位元被誤判成 symbolic link／reparse point，導致 `jdk-*/` 在安全解壓前即被拒絕。安全檢查仍拒絕真正的 symbolic link、Windows reparse point、路徑穿越與不安全項目。Windows 若在 `java -version`／`javac -version` 後短暫保留映像或防毒掃描鎖，正式目錄的同磁碟原子搬移只會針對 Access／Sharing／Lock violation 做有界重試；其他 I/O 錯誤立即失敗。
- Core／Online 對話框的 busy ListBox 使用明確的深色 theme template；讀取版本、推薦或搜尋時不再回退 Windows 白色 Disabled 視覺。Server 清單右鍵移除入口、二次確認與確認後關閉轉場也維持深色資源，不顯示短暫白塊或白窗。
- FTB Installer 的 ANSI 控制序列與游標更新會先正規化；主要下載進度與「速度／預估剩餘時間」分成上下兩行，不再把重複 `[0m`／`[90m` 串成長列。
- 下載並行度針對高速網路調整：FTB 使用 16 線；Modrinth 預設 12 線、硬上限 16 線。平行下載任一工作首次失敗時會取消同批工作，等待已啟動工作收斂後清理 staging，不留下假完成或背景下載。
- 0.4.2 是全新 Release 位元；最終測試數、live smoke、hash、簽章、時間戳與封裝結果均已由凍結後成品重新取得，沒有沿用 0.4.1；詳見 `docs/測試報告-0.4.2.md`。

## 0.4.1 核心版本清單與線上模組包 Hotfix

- App／Core、Assembly、FileVersion、ProductVersion、manifest、視窗版本與三組 Provider User-Agent 升為 `0.4.1`／`0.4.1.0`；預定正式檔名為 `Muhun MCSV Manager 0.4.1.exe`。同目錄可攜式備份新增 0.4.1 排除，並保留 0.4.0、0.3.1、0.3.0、0.2.5 與舊 `MinecraftServerManager.exe` 相容排除。
- 事故基線現包含七筆 Windows `.NET Runtime` 事件 1026：0.3.0 線上模組包曾有三筆 `ProgressPercentage` 同型 binding 閃退；0.4.0 核心建立器另有四筆獨立現場事件，皆在選擇具有版本列的核心時，由 `Run.Text` 的預設 TwoWay／OneWayToSource 行為嘗試寫回 `CoreServerVersion.BuildDisplay` 唯讀屬性，於 WPF template layout 中拋出 `XamlParseException`／`InvalidOperationException` 並終止 GUI。
- 核心版本列的 `MinecraftVersion`／`BuildDisplay` 與線上模組包版本列的唯讀顯示屬性全部明確使用 `Mode=OneWay`。新增 WPF `Run.Text`／其他預設 TwoWay dependency property 的唯讀來源契約掃描，並以獨立 STA 真正建立版本容器、執行 measure／arrange／layout，避免只解析 XAML 或只顯示空清單而漏掉 template binding 失敗。
- 線上模組包新增 FTB／Modrinth 自動熱門推薦：視窗初次顯示載入 FTB featured，切換到 Modrinth 時以官方搜尋排序取得熱門項目。FTB、Modrinth 均保留名稱搜尋；選取結果後可讀取實際版本與官方 Server Pack／`.mrpack` 可用性。
- CurseForge 維持 BYOK：推薦、搜尋及版本讀取都只在使用者提供當次 API Key 後執行；Key 仍不保存、不記錄、不進 URI／命令列，也不傳給檔案 CDN。未提供 Key 時顯示可操作提示，不把憑證缺失呈現成「沒有推薦」。
- 熱門推薦、搜尋與版本載入屬於無法量化的網路作業，進度列改為 indeterminate 並隱藏百分比，不再顯示假的 `0%`；只有具有實際量化值的安裝階段顯示百分比。
- 0.4.1 是全新 Release 位元；最終測試數、live smoke、hash、簽章與封裝結果均已重新取得，沒有沿用 0.4.0；詳見 `docs/測試報告-0.4.1.md`。

## 0.4.0 統一匯入與 12 核心建立器

- App／Core、Assembly、FileVersion、ProductVersion、manifest、視窗版本與三組 Provider User-Agent 升為 `0.4.0`／`0.4.0.0`；預定正式檔名為 `Muhun MCSV Manager 0.4.0.exe`。同目錄可攜式備份新增 0.4.0 排除，並保留 0.3.1、0.3.0、0.2.5 與舊 `MinecraftServerManager.exe` 相容排除。
- 主畫面的「匯入 Server 資料夾」與「匯入單一 Server JAR」合併為「匯入現有 Server」；點擊後由深色選擇對話框分流至原有資料夾或 JAR 流程，不降低靜態辨識、使用者確認、路徑與 reparse-point 驗證。
- 「從官方建立 Paper」擴充為「建立核心啟動器 Server」，以單一可搜尋、可取消、有進度的深色對話框動態顯示 Paper、Spigot、CraftBukkit（Bukkit）、Forge、NeoForge、Fabric、Mohist、Arclight、Velocity、Vanilla、CatServer、Akarin。沒有實際可驗證上游版本的核心不建立假版本。
- Paper／Velocity 只接受 PaperMC Fill API 的 Stable artifact 與 SHA-256；Vanilla 核對 Mojang manifest、逐版 metadata 和 Server JAR 的 SHA-1／大小。Vanilla 的接受語法範圍為 `1.0.0` 至 `26.2`，但 Mojang 實際可下載 Server JAR 的目前範圍為 `1.2.5` 至 `26.2`，更早版本因缺少官方 Server artifact 而略過。
- Fabric、Forge、NeoForge 使用各自官方 Meta／Maven 來源、checksum 與精確版本身分；Forge 歷史版只有在 exact `.sha256` 明確 404 時才改驗同一官方 Maven artifact 的 `.sha1`，格式錯誤、redirect、其他 HTTP 錯誤與雙 checksum 缺失都會拒絕。受控 installer 只在管理器 staging 內以固定引數直接執行，安裝後再次核對核心種類、Minecraft／loader 版本與可信啟動結構。
- Mohist 使用官方 API SHA-256；Arclight 只接受官方 GitHub Release 帶 SHA-256 digest 的資產。CatServer 固定官方 `1.12.2`、`1.16.5`、`1.18.2`，Akarin 固定官方 `1.12.2-R0.4.4`；舊資產同時鎖定 release／asset 身分、檔名、大小、URL 與維護端 SHA-256，避免上游替換檔案。
- Spigot／CraftBukkit 不散布預先編譯成品。管理器驗證固定的官方 Jenkins BuildTools build 200，使用受管理完整 JDK 在本機依官方 revision 編譯，再以逐版官方 JSON 的 output SHA-256 驗證結果；JRE 或缺少 `javac.exe` 的 runtime 會拒絕。0.4.0 實作快照中具有兩種輸出強驗證的 12 版為 `26.2`、`26.1.2`、`26.1.1`、`26.1`、`1.21.11` 至 `1.21.4`，實際 UI 仍依即時上游可驗證結果更新。
- 所有核心建立先進入 `servers/.core-installing-*`，重新解析 canonical product／version／build，完成來源、大小、雜湊、JAR／argument files 與靜態核心辨識後才原子提交。取消、下載失敗、版本改變或辨識不符都不加入管理清單。
- Velocity 建立結果使用 `--port` 啟動參數及 `shutdown` 停止命令，不建立 `server.properties`；其 Port 仍參與 GUI 內多 Instance 的即時衝突配置。
- 新增核心建立器 WPF STA lifecycle／layout 回歸與 `--core-dialog-smoke-test` 診斷入口；統一匯入對話框亦涵蓋 owner 跨 Dispatcher／STA 的安全分流。
- 核心建立器目前的 User-Agent 聯絡字串為 `contact: Muhun`，不是真實公開 URL／電子郵件，因此不宣稱完全符合 Paper 的公開散布建議；本版 Fill API live smoke 為 HTTP 200。取得維護者願意公開的網址或信箱後仍應更新，專案不杜撰聯絡資料。
- 0.4.0 已完成 Release build、Core 487／487、App 114／114、自包含單檔 publish、自我簽署 Authenticode、DigiCert 時間戳、中文與空白路徑的簽章後診斷及封裝稽核；實際 hash 與成品資料記錄於 `docs/測試報告-0.4.0.md`。

## 0.3.1 線上安裝視窗閃退修正

- 0.3.0 正式現場在 2026-08-17 00:16:54、00:17:35 與 00:19:04（Asia/Taipei）三次重現：開啟「線上安裝模組包」時，WPF 將 `ProgressBar.Value` 的預設 TwoWay binding 套到 `OnlineModpackViewModel` 的唯讀 `ProgressPercentage`，拋出未處理 `System.InvalidOperationException` 並終止 GUI。Windows `.NET Runtime` 事件 1026 的 stack 位於 `PropertyPathWorker.CheckReadOnly`／`DataBindEngine`／layout-render；`KERNELBASE.dll` 與 `0xe0434352` 只是 managed exception 的外層 crash 簽章，Code Integrity 不是根因。
- `ProgressBar.Value` 改為明確 `Mode=OneWay`，只由 ViewModel 將安裝進度推送到 UI，不再要求 WPF 寫回沒有 public setter 的屬性。
- 新增會實際建立並 layout 線上安裝 Dialog 的 WPF STA 回歸測試，不只檢查 XAML 中存在 ProgressBar；測試必須讓 data binding engine 完成 attach，才能攔截同類唯讀屬性 binding 例外。
- App／Core、Assembly、FileVersion、ProductVersion、manifest、視窗版本與 Provider User-Agent 升為 `0.3.1`／`0.3.1.0`，正式檔名改為 `Muhun MCSV Manager 0.3.1.exe`，避免覆寫或命中已知有缺陷的 0.3.0 快取。
- 同目錄可攜式備份排除新增 0.3.1 檔名，並保留 `Muhun MCSV Manager 0.3.0.exe`、0.2.5 與舊名稱，避免舊正式 EXE 被包入 Server／恢復點 ZIP。
- 0.3.0 視為已撤回的有缺陷版本；其歷史報告與 hash 僅供鑑識，不代表仍建議使用。0.3.1 的 Release build、完整測試、成品、hash 與 Authenticode 尚待重新執行，`docs/測試報告-0.3.1.md` 保留明確 `PLACEHOLDER`，不沿用 0.3.0 成果。

## 0.3.0 線上模組包匯入與可靠性復原

- 產品、Assembly、FileVersion、ProductVersion、內嵌版本字串與應用程式 manifest 更新為 `Muhun MCSV Manager 0.3.0`／`0.3.0.0`；Company、Authors 與著作權持有人維持 `Muhun`。同目錄 Server 備份排除規則加入 `Muhun MCSV Manager 0.3.0.exe`，並保留 0.2.5 與舊名稱相容排除。
- 新增 FTB、CurseForge 與 Modrinth 線上模組包搜尋、版本選擇、下載、staging 安裝、進度／取消及安裝完成後直接加入管理清單。
- FTB 流程只下載並執行通過 SHA-256 與 Authenticode 驗證的官方 Windows x64 Server Installer；安裝後再次比對 Pack／Version ID 與靜態偵測到的 Server 啟動結構。
- CurseForge 採 BYOK：API Key 只從 `PasswordBox.SecurePassword` 複製到當次 awaited API 呼叫，不保存至設定、紀錄、URI 或命令列，也不傳給檔案 CDN。只接受選定 client file 明確連結且可驗證下載的官方 Server Pack；缺少官方 Server Pack、禁止第三方散布或下載位址不可用時 fail closed。
- Modrinth 安全驗證 `.mrpack` 索引、server-side 檔案與 hash，再從 Mojang／Fabric／Forge／NeoForge 官方來源取得對應 Server Loader。0.3.0 暫不支援 Quilt：官方 Installer CLI 無法可靠傳遞所有失敗，且 server libraries 缺少管理器可強制核對的官方 hash，因此不把可能不完整的結果註冊為 Server。
- 所有線上安裝先進入管理器擁有的 `.installing-*` staging，只有安全解壓、來源身分、Minecraft／Loader 版本與可信啟動方式全部通過才搬入正式資料夾。流程不執行 Pack 自帶 BAT／SH／PS1、自訂安裝腳本或 JAR wrapper；受控的官方 FTB／Loader Installer 以 `UseShellExecute=false` 與明確 argument list 直接執行，不經 shell。
- 新增 Crash 與 Hang 的分離處理。Crash 由 Java 程序非正常退出觸發；Hang Watchdog 以 Minecraft 狀態協定被動探測目前 Session，尊重 `enable-status=false`，不會傳送 `list`。達連續失敗門檻後先傳送 `stop`，等待 30 秒，逾時才強制終止完整 Java Process Tree，並在報告中區分安全退出與強制終止。
- 新增每 Instance 的自動重啟熔斷器：10 分鐘視窗內第 1／2／3 次分別延遲 5／15／45 秒，第 4 次停止自動重啟；單一 Session 穩定執行滿 10 分鐘會清除歷史。手動停止與政策關閉會取消尚未開始的重啟，舊 Session 不能重啟或污染新 Session。
- 新增有界、遮罩密鑰的崩潰診斷：每次 Crash／Watchdog 復原在 `crash-reports` 產生 `report.md`、`report.json` 與 `console-tail.txt`，彙整最新日誌、原生 crash 候選、自檢結果、疑似模組及處理建議，但不會自動刪模組、覆寫世界或靜默還原。
- 新增可選的定期健康恢復點。Server 健康運行時先 `save-off`、`save-all flush` 並等待確認，再建立有界 ZIP；逾時不發佈 ZIP，所有已停用存檔的結束路徑都會嘗試 `save-on`。備份期間若 Session 改變，不一致 ZIP 不會保留為可用恢復點。
- 新增從健康恢復點建立新 Server 副本的安全還原流程；目的地必須是全新名稱且位於明確信任的 `servers` 根目錄，原始 Server／世界永不覆寫，復原副本的自動重啟、Watchdog 與定期恢復點預設關閉。
- Server 清單移除改為項目右鍵選單與「確認／取消」二次確認，不再要求輸入名稱；只允許移除已停止的項目，且只修改管理清單，不刪除原始資料夾、世界或備份。
- `manager.json` 目前 schema 提升為 4；schema 1–3 與缺少可靠性欄位的設定保持相容，新 Watchdog／健康恢復點功能預設關閉。
- 0.3.0 已完成 Release 建置、471 項自動測試、中文／空白路徑診斷與 FTB 實際資料夾辨識；正式單檔 EXE 以 `CN=muhun`、SHA-256 Authenticode 與 DigiCert 時間戳簽署，詳細雜湊與驗證結果見 `docs/測試報告-0.3.0.md`。

## 0.2.5 Muhun 品牌與應用程式識別

- 產品名稱與視窗標題更新為 `Muhun MCSV Manager`，版本更新為 0.2.5。
- Windows 檔案 metadata 的 Product／Title 設為 `Muhun MCSV Manager`，Company／Authors 設為 `Muhun`，著作權設為 `Copyright © Muhun 2026`。
- 正式建置輸出的 EXE 基名更新為 `Muhun MCSV Manager 0.2.5.exe`；診斷指令及同目錄可攜式備份排除規則同步支援新版名稱，並保留舊檔名排除以相容既有安裝。
- 新增原創深色立方伺服器應用程式圖示：由透明 PNG 母稿產生含 16、20、24、32、40、48、64、96、128、256 pixels 的 10 尺寸、32-bit PNG-compressed Windows ICO。
- WPF 專案以 `ApplicationIcon` 嵌入該 ICO；self-contained single-file 發佈後由 EXE 自身提供檔案總管、工作列、捷徑與視窗圖示，不需要在 EXE 旁散佈 loose PNG／ICO sidecar。
- 新增 Windows ICO 結構契約及產品 metadata 自動測試，防止後續建置遺失圖示尺寸、品牌名稱、版本、檔案說明或 Muhun 著作權。
- Provider User-Agent 與程式內版本資訊同步更新為 0.2.5。

## 0.2.4 深色對話框與全域外觀設定

- 匯入 Server Pack、匯入單一 JAR、Paper 版本選擇及外觀設定等所有自訂對話框改用明確的深色視窗樣式與動態佈景資源；修正原生白色 Window 背景搭配淺色文字所造成的低對比與內容難以閱讀。
- 主視窗右上角新增齒輪入口，可在同一個設定視窗自訂 Window、Panel、Raised、Border、Accent、AccentDark、Text 與 Muted 的 `#RRGGBB` 色碼。
- 新增 None、Dots、Grid、Diagonal 四種背景圖案，以及圖案色、圖案透明度、全域背景圖片與圖片透明度。背景圖片會先完成真實格式、最大 64 MB、最大 64,000,000 解碼 pixels 及 no-reparse 驗證，再複製到管理器擁有的 `themes` 子樹。
- 外觀編輯採可回復的交易流程：「預覽」暫時套用動態資源、「取消」完整還原開啟視窗前的配色與背景、「儲存」才持久化；「恢復預設深色」也只先預覽，必須明確儲存才會生效。
- `manager.json` 升級為 schema 3 並加入全域 `Appearance`；schema 1／2 及沒有該欄位的既有設定保持向後相容，無效或缺少的外觀值會安全回退到預設深色，不影響既有 Server Instance 清單。
- 新增外觀服務、ViewModel、JSON round-trip、舊 schema、背景圖片與路徑安全測試，並以 Windows UI 實際操作檢查齒輪入口、深色對話框、捲動、預覽與取消還原。預設色盤以深色可讀性為目標；使用者自行設定的任意色彩組合不宣稱必定符合 WCAG。

## 0.2.3 跨版本／核心玩家即時偵測

- 玩家事件改為依每個 Instance 的 `CoreType` 分流；Minecraft Server、Velocity 與 BungeeCord／Waterfall 不再共用一組模糊規則，後端 Server 切換也不會被誤判成代理端玩家離線。
- 補上 Paper／Spigot 1.12.x 的 `logged in with entity id` 成功登入事件；實際 Paper 1.12.2 即使沒有輸出 `joined the game`，玩家也會即時加入清單。
- Forge／NeoForge 日期前綴改為有界、語系中立的解析；已驗證 zh-TW Java 所產生的 `8月` 日誌，以及新舊 Minecraft logger、IPv6 endpoint、ANSI/Jansi 與 Folia thread。
- 新增 Velocity `[connected player]` 與 BungeeCord／Waterfall `InitialHandler`／`UpstreamBridge` 的權威連線事件，並拒絕 pre-login、插件偽造 logger、Velocity backend connection 與 Bungee backend switch。
- 玩家事件仍只被動接收目前 GUI 管理的 stdout／stderr，具有 Instance、CoreType 與 Session 三層隔離；沒有新增 `list` 輪詢或任何背景查詢。
- 控制台進入 UI 前新增每個 Instance 4,096 行的 drop-oldest 有界佇列，分批顯示最新輸出；玩家事件直接更新每個目前 Session 的 thread-safe、4,096 人有界權威線上集合，UI 只接收合併後快照，不再為每行建立一個無上限、高優先 Dispatcher 工作，也不會因丟棄 leave 事件留下幽靈在線。
- 玩家快照 drain 使用 `DispatcherPriority.Background`，而啟動／停止／換 Session 狀態仍使用 `Send`；即使錯誤插件或 bot 持續製造有效登入離線事件，也不會長期壓過 WPF 的輸入與 Render。
- 只有事件、尚未出現在 usercache／OP／白名單／封禁資料中的玩家會在離線後從內部集合移除；已登錄玩家仍可透過「顯示已知玩家」查看，避免長時間運行後累積純事件歷史列。
- 個人 Windows 發佈檔加入 `CN=muhun` 自我簽署 RSA Code Signing Authenticode 與 DigiCert 時間戳；目前使用者已信任其公開憑證，最終簽章位元可通過 App Control 與中文／空白路徑診斷。PFX／私鑰不會放入任何發佈包。

## 0.2.2 Unicode 路徑與控制台編碼

- 修正 Paper 1.12.2／Paperclip 在舊版 Java 11 下，因 Server JAR 使用中文絕對路徑而出現「載入 Java 代理程式時發生錯誤」的問題。通過存在性、Server 根目錄限制及 reparse-point 驗證後，管理器會從 Server 工作目錄以相對 JAR 路徑啟動，不需要把資料夾改成英文。
- 可執行 JAR 的路徑安全規則同步收緊：工作目錄外、路徑逃逸或經 junction／symlink 指向其他位置的核心會在啟動前被拒絕。
- 控制台改為逐行自動辨識嚴格 UTF-8 與 Windows 本機碼頁，兼容 Java 21 UTF-8 輸出及 Java 8／11 的 CP950 等原生啟動錯誤；stdout 與 stderr 各自保序處理，並保留跨區塊與 EOF 尾行。

## 0.2.1 玩家即時清單與 EULA 偏好

- 玩家管理預設只顯示目前線上玩家：登入即時加入，離線／斷線即時移除，不再讓 `usercache` 歷史名稱堆滿清單；可主動勾選「顯示已知玩家（含離線）」查看 OP、白名單與封禁資料，並保留手動輸入離線玩家名稱的管理方式。
- 玩家登入／離線事件改為在背景解析並以高優先 UI 佇列套用，避免大量控制台輸出延遲名單更新；本機玩家資料重新整理也不會覆蓋期間發生的即時狀態。整個流程絕不自動傳送 `list`。
- 被動事件解析新增 ANSI／Jansi 色碼，以及 Paper／Spigot 1.12、舊版與新版 Forge／NeoForge 常見 logger／marker 前綴支援；標準輸出與錯誤流都會依 Instance 及 Session 嚴格隔離。
- 依使用者指定的個人偏好，啟動 Server 時會自動確保 `eula=true`，不再顯示重複同意視窗；判定及寫入都在 Server 資料夾獨占鎖內完成。
- 已為 true 的 EULA 文件保持原始位元內容不變；false 或缺少屬性時才保留原編碼、BOM 與其他內容，以備份、flush 及原子替換更新。

## 0.2.0 Server Pack 與管理介面更新

- 新增已安裝完成的 FTB、Forge 及 NeoForge Server 資料夾原地匯入；不複製模組、世界或既有 EULA 檔案。
- 新增執行主機 OS 自動判斷：Windows 使用 `run.bat`／`win_args.txt`，Linux Core 策略使用 `run.sh`／`unix_args.txt`；目前發佈的 WPF GUI 仍僅支援 Windows。
- 新增 FTB manifest、啟動腳本、JVM args 及內附 Java metadata 的嚴格、有界靜態解析。
- 新增不經 shell 的 Java argument-file 啟動，同時保留控制台、安全停止、資源監控及多 Instance 隔離。
- GUI 位於可辨識的 Server Pack 根目錄時，第一次執行會自動偵測並要求確認，不會靜默啟動。
- 新增 Port 衝突處理：讀取 `server.properties`，結合本機 TCP／UDP 使用狀態與其他啟動中、執行中或等待啟動 Instance 的 Port；停止中的 Instance 不永久保留，衝突時從 `25565` 起選擇第一個可用值，只更新目標 Server，不停止其他程序。
- 新增 `server-port` 安全寫入：既有檔案先建立 `.bak`，再以同資料夾暫存檔、flush 及替換完成更新。
- 新增每個 Server 資料夾的跨 GUI／跨程序獨占執行鎖；Port 與 EULA 寫入移至取得鎖後，同一份世界不能透過改 Port 被重複啟動。
- `server.properties` 備份改為不覆蓋既有 `.bak`，需要時依序建立 `.bak.2`、`.bak.3`。
- 新增控制台智慧自動捲動：停留底部時跟隨最新輸出，向上閱讀時暫停，回到底部後恢復；雙控制台各自保存狀態。
- 新增獨立「外觀」頁籤，可預覽、替換及清除每個 Instance 的背景與清單圖示；原始圖片不會被移動或修改。
- 新增同目錄可攜式配置的管理器檔案備份排除規則。
- 新增 Windows／Linux、路徑安全、惡意腳本、啟動參數、被動玩家事件、資料夾鎖、Port 配置與 `server.properties` 更新測試。

## 0.1.0 MVP

- 初版 Windows WPF 多伺服器管理器。
- 可攜式受管理 Java Runtime，以及含雜湊驗證的 Paper Stable 下載。
- 靜態核心辨識、隔離控制台、程序監控、安全停止／重啟及備份。
- `server.properties` 原始編輯器，以及每個 Instance 的背景／圖示、Java 與備份頁籤。
- 唯讀 Modrinth 插件／模組相容更新查詢。
- 診斷 smoke test 與可重現的畫面預覽模式。
- 自動化安全與 Provider 測試。
