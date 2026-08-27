# 架構

```text
MinecraftServerManager.App (WPF/MVVM)
├─ MainWindow / 對話框
├─ MainWindowViewModel
├─ 每個 Instance ID 一個 ServerInstanceViewModel
├─ 每個 Instance 一份 2,000 行 UI tail／100 ms、4,096 pending 的 Console 與 Diagnostic 批次投影
├─ 各控制台面板獨立且尊重離尾狀態的尾端跟隨
├─ CoreType／Instance／Session 分流、100 ms 合併的即時線上名單 + lazy 背景已知玩家檢視 + 管理指令
├─ 每個 Instance 的外觀預覽 / 受管理主題副本
├─ 全域 AppearanceThemeService / 動態色彩、圖案與受管理背景資源
├─ 可取消的外觀設定交易（預覽 / 還原 / 儲存 / 恢復預設）
├─ ExistingServerImportChoiceDialog / 700×500 可縮放、對齊且不裁切的資料夾與單一 JAR 安全匯入分流
├─ OnlineModpackDialog / FTB、Modrinth 熱門推薦、搜尋與 provider-neutral workflow
├─ CoreServerCreationDialog / 12 核心目錄、版本搜尋、進度、取消與 provider-neutral workflow
├─ Official / Hybrid / Spigot Core Creation Backends
├─ 每個 Instance / Session 的 Crash、Hang Watchdog、恢復點與生命週期協調
└─ EXE 旁的可攜式資料路徑

MinecraftServerManager.Core
├─ Models               僅包含可保存的設定（manager.json schema 5）
├─ Services             JSON、安全路徑、JAR/Server Pack 靜態辨識、Java 規則、玩家事件、EULA、Watchdog 與崩潰診斷
│  ├─ 依當下快照配置 TCP/UDP Port
│  └─ 原子更新 server.properties Port
├─ Providers            Paper/Velocity、Mojang、Fabric/Forge/NeoForge、Mohist/Arclight/CatServer/Akarin、Spigot BuildTools、受管理 MinGit、Adoptium、FTB、Modrinth、經驗證的下載與受控官方 Installer；CurseForge 低階 Provider 僅作 Core 相容實作，不接 production UI
└─ Runtime
   ├─ ServerProcessManager
   ├─ 每個 Session 的 stdout／stderr severity 與 multiline diagnostic 分類器
   ├─ 每個 Instance 的鎖與每次啟動的 Session ID
   ├─ 每個 Server 根目錄的跨程序獨占檔案鎖
   ├─ 重新導向的 stdin/stdout/stderr、逐行 UTF-8／Windows 本機碼頁解碼與有界紀錄
   ├─ 程序 / 資源生命週期
   ├─ BackupService
   └─ BackupRestoreService（只提交全新復原副本）
```

## 0.4.10 系統匣視窗生命週期

0.4.10 只擴充 WPF 主視窗的呈現生命週期，不建立獨立 host、Windows Service 或可脫離 GUI 的 Java 程序管理層。主視窗進入 `Minimized` 時保存先前的 `Normal`／`Maximized` 狀態、顯示 `NotifyIcon` 並隱藏 WPF Window；雙擊圖示或按「開啟 MCSV Manager」會把要求送回視窗 Dispatcher，重新顯示、還原原狀態、隱藏系統匣圖示並啟用視窗。系統匣 callback 不可直接從非 WPF thread 操作 Window。

系統匣「結束」會先還原視窗再呼叫既有 `Close` 路徑；標題列 `X` 仍是實際結束，不改成最小化。兩個入口都必須共用原有 running Server 確認、背景工作取消／收斂、`ShutdownAsync`、設定儲存及安全停止語意。結束或 Window 關閉後要將系統匣圖示設為不可見並冪等釋放 native／WinForms 資源，避免 Explorer 留下殘影圖示或 callback 存取已關閉 Dispatcher。

如果系統匣 adapter 在應用程式啟動時無法建立，程式會 fail-soft 保留一般 WPF 最小化而不終止應用程式；這項降級已完成並由 lifecycle 測試驗證。0.4.10 不加入 Server 無人自動停止、Minecraft Port listener、登入封包解析或連線喚醒；`0.5.0-preview.1` 的 idle／wake 架構不屬於此正式版本線。

## 0.4.9 高頻事件、批次呈現與主機效能邊界

Core 的輸出擷取與 WPF 呈現是兩條不同責任鏈。`ServerProcessManager` 仍持續讀取 stdout／stderr、完成 session severity 分類並保存有界 retained log；App 不以停止擷取、阻塞 pipe 或限制 Java 工作量來維持畫面順暢。跨執行緒事件先進入每個 Instance 的 pending buffer，UI 每 100 ms 最多提交一個批次。buffer 在 4,096 筆後維持有界，畫面只建立最新 2,000 行的 canonical tail，再從同一批 view model 投影 Console 與 Diagnostic；一次批次只引發一次 collection Reset。大量舊行裁切使用 `RemoveRange`，避免從 List 頭部逐筆移除形成 O(n²)。

排程與呈現具備 session 邊界及 latest-wins 語意。只允許目前 Instance／Session 的資料提交；資源採樣每個 Instance 只保留最後值，UI 一次套用 CPU、Memory 與 Uptime 等相關屬性。玩家 presence 先在執行緒安全 buffer 合併並以 100 ms cadence 發布，待處理人數上限 4,096；批次建立最終名單後以一次 Reset 取代數千次 add/remove notification。已知玩家 JSON／磁碟讀取在第一次進入玩家分頁時於背景執行；改選 Server、離開分頁或提出新的重讀請求會取消舊工作，generation/latest-wins gate 阻止晚到結果覆蓋新狀態。「顯示已知玩家」只在 UI 執行緒上切換已載入資料與線上名單的投影，不啟動或取消讀取。

自動捲動以每個面板是否接近尾端為準。使用者向上查看歷史時，新批次不呼叫 `ScrollIntoView` 把畫面拉回；回到底部後才重新追蹤。玩家 presence parser 對普通輸出先走便宜的必要 token fast gate，只有可能是登入／離線事件時才執行完整玩家規則；severity classifier 仍逐行維持 0.4.8 的保守分類、unknown stderr、`DiagnosticId` 與 session reset 契約，不將 presence gate 誤用為錯誤分級。

`Application.Current` 在 App.xaml BAML 完成前就可能對程序可見，因此 ViewModel 不得只因它非 null 就修改全域 Resources。主題套用要求目前執行緒具有 Application Dispatcher access；production `App.OnStartup` 符合此條件，背景與 headless 初始化只修復設定模型、不觸碰 Dispatcher-owned ResourceDictionary。App 測試只建立一個載入正式 BAML 資源的 concrete `App`，但只執行 `Dispatcher.Run`、絕不呼叫 `Application.Run`，所以不會觸發 production `OnStartup` composition；整個測試 assembly 亦序列化，避免 process-global WPF state 與 headless tests 交錯。

OneDrive 提醒只用設定的同步根與 canonical Server 路徑作無 I/O 判斷，不追蹤 link 或在選取時掃描磁碟。命中時 UI 提醒 active Server 的 world、region 與 log 高頻寫入會與雲端同步競爭 I/O。管理器不自動停服、搬移資料、停止 OneDrive、調整程序優先權或限制 JVM／CPU／RAM；安全處置是由使用者先停止 Server，再自行搬到不受同步的本機目錄。

## 0.4.8 console severity、diagnostic 分流與呈現邊界

stdout 與 stderr 是傳輸通道，不是嚴重度。0.4.8 的 `ServerProcessManager` 為每次 Session 各自建立分類狀態，先以有界規則移除 ANSI 呈現後綴，再辨識 Minecraft／Forge／NeoForge／Log4j 及 JVM 的明確 `INFO`、`WARN`、`ERROR`、`FATAL` marker。出現在 stderr 的明確 `INFO` 仍是 Information；沒有可信 marker 的 stderr 則保留 `Unclassified` severity，UI 以非紅色的 `STDERR` 標籤呈現，不會因通道就提升成 Error。

管理器自己知道語意的 timeout、停止與自動重啟失敗等事件，不經文字推測，而是建立時就明確指定 severity。一筆 `Warning`、`Error` 或 `Fatal` 根行會取得 `DiagnosticId`；可辨識的 stack trace、`Caused by`、suppressed 與其他後續行共用同一個 ID 與 severity。`StartsDiagnostic` 只標記事件根，後續行仍完整顯示；分類狀態不跨 Session 沿用，舊 Session 輸出也不能汙染新 Session。

App 對每個 Server 只保留一份 2,000 行時間順序 history；`ConsoleLines` 與 `DiagnosticLines` 是對同一批 line view model 的即時呈現，不是兩份獨立日誌。選項切換時會立即 reflow，不遺失順序或重複累積；diagnostic 行數與事件數分開顯示，同一 `DiagnosticId` 的多行區塊只計一個事件。Core 的有界 retained log、crash 診斷、玩家事件解析與命令路由不因 UI 分流而改變。

`ServerInstance.SeparateDiagnosticOutput` 是 schema 5 的 nullable per-Server 設定。舊 JSON 缺欄位或值為 `null` 時 effective false：不顯示「錯誤／警告」分頁，Console 維持全部輸出混合。只有值為 `true` 時才顯示分頁，並將嚴格分類為 `Warning`、`Error`、`Fatal` 的行從 Console 移出。新建與匯入流程在首次持久化前將缺值正規化為 `true`；載入舊記錄不會擅自改寫。分頁選取使用穩定 key，從伺服器設定頁開關時不會因 index 位移而跳頁；若關閉時正在 diagnostic 分頁，才回到 Console。雙控制台只在兩個 Server 都 opt in 時分流，否則合理降級為混合顯示。

Forge 26.x／NeoForge 26.x 會將部分 Netty、Log4j 或 terminal 的非致命訊息寫到 stderr。0.4.8 不再把這些行因 stream 全部標紅；只有明確的 strict severity 才進入 diagnostic 分頁。這個呈現層邊界與 0.4.7 的 runtime headless 組合邊界彼此獨立，不會重建 Server、改寫官方 arguments 或改變 provenance。

## 0.4.7 runtime headless 啟動邊界

Minecraft 的 `nogui` 是 Server application argument，不是 JVM option；Windows `ProcessStartInfo.CreateNoWindow` 只能抑制作業系統 console，不能阻止 Minecraft 自己建立 AWT／Swing 管理視窗。Forge／NeoForge 官方 `run.bat`／`run.sh` 通常把呼叫端參數留給結尾的 `%*`／`$@`，因此靜態偵測可能正確得到空的持久化 `ServerArguments`。0.4.7 在 `JavaJarLaunchDefinitionResolver` 組合最終命令時，先原樣加入已驗證的 `-jar` 或 JVM `@argument-file` 及使用者 Server arguments，再針對專用 Minecraft 核心檢查是否已有大小寫不敏感的 `nogui`；缺少時只在最尾端補上一個。

這是 runtime composition，不是安裝或 migration。解析器不改寫 `ServerInstance.ServerArguments`、`manager.json`、官方 `win_args.txt`／`unix_args.txt`、Installer 輸出、hash 或 typed provenance，也不重新下載、重新安裝或重建 Server。`nogui` 位於所有 JVM argument files 之後，不能變成 JVM option或改變官方 Loader 參數；已有 `NoGui` 等異體時不重複。Velocity、Unknown 與 Custom JAR 不在注入集合，避免替未知 application 猜測其 CLI 契約。

正向契約同時覆蓋 executable JAR 與 Java argument-file 啟動；負向契約覆蓋既有 `nogui`、設定物件不可變，以及 proxy／unknown／custom 不注入。實機 Forge 26.2 與 NeoForge 26.2 啟動皆到達 `Done`、額外 Minecraft server 視窗為 0，送出 `stop` 後程序 exit code 皆為 0。

## 0.4.6 typed provenance、背景工作與提交邊界

官方 Installer／BuildTools 產物不再一律回退到只懂傳統可執行 JAR 的通用偵測。每條可執行路徑都先由強來源證據建立 typed provenance，promotion 前再重驗所有檔案 fingerprint：現代 Spigot／CraftBukkit bootstrap JAR 以官方 output SHA-256 或官方 refs 加嚴格本地證據確認；Fabric 可接受官方 Installer 產生、在 `fabric-server-launch.jar` 內嵌 properties 的版面；NeoForge 可接受含精確 `--fml.neoForgeVersion`、`--fml.mcVersion`、`--fml.neoFormVersion` 的 direct-main 啟動；Forge shim 必須逐位等於強雜湊 Installer 的內嵌項目。這些窄化例外只存在於已驗證的官方 workflow，不會降低本機未知 JAR、模組包 wrapper 或任意 argument file 的通用安全門檻。

Spigot／CraftBukkit 目錄分成兩種明確證據模式。現代 12 個 stable aliases 有上游 output SHA-256，建立後必須完全相等；舊版 55 個 aliases 沒有可用的上游輸出雜湊，因此 UI 明確標示為官方 refs 模式，使用不可變四 refs、固定 BuildTools 與受管理 MinGit、隔離工作環境、建置前後 HEAD 重驗、產品專屬 JAR 結構檢查，以及 promotion 前後不變的本機 SHA-256。兩者合計 67 筆，涵蓋 `1.8` 至 `26.2`，但不把本機 hash 誤稱為官方 output hash。

核心及模組包選擇視窗只建立不可變的工作定義；實際下載、Installer、BuildTools、驗證與 staging 由 application-lifetime `BackgroundServerJobCoordinator` 執行。主 GUI 不因工作執行而 modal 阻塞，可連續加入多筆。工作中心只是同一協調器的非 modal view；關閉工作中心不會取消工作。主視窗底欄顯示 active aggregate，`☰` 重新開啟完整清單。高頻 progress 採單一排程旗標與 latest-value slot 合併，10,000 次 burst 不會等量占用 WPF Dispatcher。

排程器依主機資源選擇 global／BuildTools 上限；96 GiB 以上且至少 24 logical processors 時為 10／3。General 與 BuildTools 使用分離的 unbounded channels，各自只有固定 worker，最後共同取得 global semaphore；因此 BuildTools backlog 不會先占滿所有 general readers，總 active 數仍不會突破全域上限。App shutdown 停止接收、完成 channels、取消並等待 workers 收斂，工作不得在 workflow disposed 後繼續。

資料夾與管理記錄是兩階段提交。背景 workflow 及單一 JAR 匯入先在管理器擁有、名稱不可預測的 staging 完成；共同 registry gate 內重新檢查 NFKC／case-insensitive 名稱與 canonical target identity，再以單次 `Directory.Move` promotion，持有 gate 完成 Port、設定保存與清單加入。清理只能作用於流程確定擁有的 staging 或已成功 promotion 的 final，不得因同名碰撞刪除其他工作已提交的資料夾。Adoptium 另以 canonical Runtime destination 作跨 provider instance gate；同目的地 waiter 會重用並重新驗證第一筆安裝，不同 Java 種類／版本仍可並行。

網路工作採 workload-aware parallelism。Modrinth 依 manifest 檔案數及總大小選擇 1／2／4／8／12／16 workers，FTB 使用高吞吐固定工作數；第一個不可恢復錯誤會取消同批工作，但仍等待已啟動工作清理 `.partial` 與 staging。正式 UI 與 production workflow 僅有 FTB／Modrinth，CurseForge 相容 Provider 仍不得從公開入口到達。

## 0.4.5 BuildTools JVM 與官方 Loader 子程序邊界

BuildTools 的 Java 版本規則除了選出可用 JDK，也決定 JVM 穩定策略。Java 25 以上的 BuildTools operation 會在清除 ambient 環境後，透過受控 `_JAVA_OPTIONS` 同時傳入 `-XX:TieredStopAtLevel=1` 與真 LF `line.separator`；前者讓主程序及其 Java／Maven 子程序使用 C1 編譯層級，降低現場 HotSpot JIT compiler replay 致命失敗的風險，後者維持 0.4.4 的官方可重現輸出條件。Java 24 以下只傳入行尾設定，不任意改變既有 JIT 策略。無論 JVM 模式為何，四個 repository refs post-check 與官方 Spigot／CraftBukkit output SHA-256 equality 都仍是 blocking gate。

共用的 bounded process host 不等於共用產品錯誤。Spigot runner 捕捉非零結果後會建立 `SpigotBuildToolsProcessException`，以 product、Minecraft 版本、exit code 及是否截斷輸出作專用分類；不再把 BuildTools 顯示為 `ModLoader Installer`。在 operation 清除前，forensic collector 只檢查頂層一般檔案的 `replay_pid*.log`／`hs_err_pid*.log`，並從有界 stdout／stderr 辨識 compiler replay、fatal error、compiler task 等明確 marker。摘要採嚴格 allowlist：只保留有限筆數的分類 marker、artifact basename／size 與 `<WORKSPACE>`／`<TARGET>` 等位置類別，未分類原始行一律省略，不依賴可能漏掉秘密的 denylist。

鑑識與保留工作樹是兩回事。診斷摘要在記憶體中建立後，成功、取消、一般失敗與 JVM 致命失敗仍走同一個 `finally` 清理；未提交的 BuildTools operation 及外層 `.core-installing-*` 都不得留下。診斷失敗不能取代原始 process result 或阻止清理；失敗路徑若無法完整清除，cleanup failure 必須成為可見錯誤，管理清單仍不得收到半成品。

Forge／NeoForge／Fabric Loader 與 Java tool probe 共用 `ManagedJavaProcessEnvironment`。每次啟動都先 `Environment.Clear()`，再只建立由精確 Java executable 推導的 `JAVA_HOME`／PATH，以及 Windows 必要的 `COMSPEC`／`SystemRoot`；因此 ambient `_JAVA_OPTIONS`、`JAVA_TOOL_OPTIONS`、`JDK_JAVA_OPTIONS`、Maven、Gradle、Git、shell 或 PATH 注入不具繼承通道。Loader operation 另建立並驗證不是 reparse point 的 private HOME／TEMP，透過環境與 `-Duser.home`、`-Djava.io.tmpdir`、`-Duser.dir` 雙重固定；Fabric `-dir` 和 Forge／NeoForge `--installServer` 都指向解析後的絕對 output 目錄。Java／javac probe 不需要可寫 HOME／TEMP，但使用精確 executable、受控 `bin` working directory 與同一最小 allowlist。

上述隔離只縮小子程序輸入，不取代 artifact 信任。Installer 本身仍必須先通過官方 metadata／checksum，執行後的輸出樹仍受 regular-file、路徑、大小、項目數、Minecraft／Loader 身分與標準啟動結構檢查；只有驗證完成的 tree 才能從 operation 搬入 staging，最後再由 application workflow 原子提交。

## 0.4.4 Spigot 可重現建立與線上來源邊界

Spigot catalog 可在程序生命週期內快取供 UI 顯示，但 cache 不是建立授權。每次 `ResolvePlan` 都必須 fresh 取得使用者選擇的 alias JSON，再以其中的不可變數字 `VersionIdentity` 取得 `/versions/{VersionIdentity}.json`；兩份資料的 `name`、Bukkit／CraftBukkit／Spigot／BuildData 四個 refs、Spigot／CraftBukkit output hashes、`toolsVersion` 與未重新解釋的 `javaVersions` 必須逐欄相同。數字 URI 不可用、JSON 不完整或任何欄位漂移都在執行 BuildTools 前 fail closed。runner 的 `--rev` 使用數字 identity，而非可能隨上游移動的 alias。

Windows 可重現性是工具鏈契約的一部分。受管理 MinGit 以固定 URL、大小、SHA-256 與實際版本驗證；BuildTools operation 使用隔離 PATH、Maven home 及私有 global Git config，`core.autocrlf=input` 必須同時約束管理器預先準備的四個 repository 與 BuildTools 後續建立的子 repository。Java／Maven 子程序的 `line.separator` 必須包含真實 LF 字元，不能把反斜線加 `n` 的兩個字元誤當換行。BuildTools 結束後再以同一受管理 Git 對四個 repository 執行 HEAD 驗證，任何 ref 漂移都拒絕輸出。

這些關卡修正 Windows CRLF checkout／commit 對 Maven POM 與 patch 輸入的改寫；舊行為可能先完成約 5–7 分鐘本機編譯，最後才得到與官方可重現輸出不同的 JAR。0.4.4 仍把官方逐版 JSON 的 Spigot／CraftBukkit output SHA-256 equality 當成 blocking gate：實際輸出 hash 只用於比對與診斷，不得以「JAR 可開啟」、本機 refs 大致正確或編譯 exit code 0 取代官方 hash。

線上模組包的 production surface 只有 FTB 與 Modrinth。ViewModel 的 provider 選擇只能接受自身 `Providers` 集合中的這兩個值；production `IOnlineModpackWorkflow` 的搜尋、推薦、版本與安裝公開入口收到 CurseForge 時必須明確 `NotSupported`。UI、鍵盤、Automation 與診斷 fixture 不呈現 API Key 或 CurseForge 控制項，且不爬取網站。Core 內既有 CurseForge Provider 可以留作低階相容程式碼與安全測試，但不能構成隱藏的產品入口。

## 0.4.3 BuildTools 工具鏈、清理與刪除邊界

BuildTools 的第三方子工具也屬於供應鏈邊界。Spigot／CraftBukkit runner 在啟動 Java 前先取得固定版本、固定 URL、固定大小與 SHA-256 的 MinGit ZIP，使用與 Java ZIP 相同的路徑穿越／特殊項目／reparse 防護解壓，再以實際 `git version` 核對版本。只有該受管理 MinGit 的 `cmd`、`mingw64/bin`、`usr/bin` 與受管理完整 JDK `bin` 能前置到隔離子程序 PATH；Maven home 及 BuildTools working tree 也限定於本次 operation。如此 BuildTools 能直接找到可信 Git，不會下載或啟動 PortableGit SFX 原生 GUI。BuildTools 的最終 Spigot／CraftBukkit JAR 仍必須符合官方逐版 output SHA-256，工具鏈控制不取代輸出身分驗證。

核心建立進度分為 pipeline stage 與 detail activity。前者只表示取得工具鏈、執行建立、驗證及提交等穩定階段；後者承載 MinGit 下載／解壓／驗證及 BuildTools stdout。兩者都由同一個不可拖曳的 WPF 對話框呈現，不建立外部 Window／Popup，也不把沒有可信總量的本機編譯偽裝成精確百分比。

所有擁有 staging 的 workflow 都必須在回報取消或失敗前完成清理。一般唯讀檔只有在重新確認不是 reparse point 後才可清除 ReadOnly 屬性；Access denied、sharing violation、lock violation 與 directory-not-empty 可有界重試，其他例外立即失敗。外部程序先取消並 await 收斂，接著清除 `.core-installing-*`、BuildTools operation、MinGit／JDK partial／staging 或模組包 `.installing-*`；不可用 silent catch 將殘留半成品當成成功。

Modrinth 並行度由本機 deterministic workload planner 依已驗證檔案數及總 bytes 選擇 1／2／4／8／12／16，且永遠受工作數與 16 線硬上限限制。這是可測試的資源配置，不是雲端 AI 或不透明模型。首錯仍取消同批並 await in-flight；每檔 hash 與最終 staging commit 不變。單一 artifact 保持一個已驗證串流，沒有明確且安全的 Range 契約時不拆段。

右鍵「完全刪除 Server」是獨立於「從管理清單移除」的破壞性 workflow。命令目標必須使用被右鍵點擊的 Instance，而非假設目前選取列；顯示確認視窗前先以開啟的 Windows handle 擷取 Volume／File ID，並把 identity lease 持有到停止與實際刪除完成，使確認期間 rename／replacement 無法被誤認為原目標。刪除器另持有信任邊界連結及解析後目標、每層祖先與目前節點的 no-follow handle，只接受相同檔案系統身分並以 handle-based delete disposition 移除；子 reparse point 不會被走訪。

永久刪除的 lexical 與 handle 關卡共同拒絕裝置／extended-path 語法、8.3／別名繞過後的危險身分、磁碟與個人／管理器重要根本身或其祖先、Windows／System／Program Files／ProgramData 的所有子樹、reparse 根與 redirecting intermediate，以及和任何其他受管理 Server 相同或任一方向祖先／子孫重疊的目標。合法個人資料根下的 Server 子目錄仍可使用；只有原本確認的目錄成功消失後才可移除 `manager.json` 記錄，任何拒絕或失敗都保留原管理項目。

## 0.4.2 Java ZIP、WPF theme 與下載協調

Java ZIP 的封存屬性必須依來源平台分流解讀。Unix external attributes 只有在檔案類型位元明確為 symbolic link 時才拒絕；一般目錄與一般檔案不得因含 Unix mode 權限位元而被當成 Windows reparse point。Windows reparse attribute、真正連結、絕對路徑、`..` 穿越及解壓根目錄逃逸仍維持 fail closed。這條規則同時適用受管理的 Temurin 16／17／21／25，不對單一 JDK 檔名設例外。完成強雜湊、解壓及 `java`／`javac` major 驗證後，staging 到正式 runtime 的同磁碟 rename 可針對 Windows Access／Sharing／Lock violation 做最多八次、尊重取消的短暫重試；來源消失、目的地出現或其他 I/O 錯誤不得重試。

WPF busy 狀態不能依賴作業系統的預設 Disabled theme。Core／Online 的清單與移除流程必須以應用程式的 Window／Panel／Text／Muted／Border 動態資源完成 Disabled、busy 與 close transition，確保使用者自訂主題仍可控制顏色，且不以白色系統模板閃現。

模組包下載採有界平行與結構化取消。FTB 可使用 16 個工作；Modrinth 預設 12、最高 16。第一個不可恢復錯誤會取消同批剩餘工作，但 orchestrator 必須 await 已啟動工作收斂，完成暫存檔與 staging 清理後才回報失敗。進度輸出先清除 ANSI SGR／游標控制，再將主要階段與速度／ETA 分行呈現；控制台文字不能直接作為可信完成訊號。

## 不變條件

1. 每個指令、控制台訊息、狀態轉移及資源樣本都帶有不可變的 Instance ID；程序事件另帶有每次啟動專屬的 Session ID。
2. 未知 JAR 一律採靜態辨識。辨識結果不會自動授權執行 installer 或任意檔案。
3. Java 以 `UseShellExecute=false` 和 `ProcessStartInfo.ArgumentList` 直接啟動；不使用 `cmd.exe /c` 或手工組合的 shell 指令。
4. FTB／Forge／NeoForge 腳本視為不受信任的文字。受限的 allow-list parser 只抽取已知 Java 呼叫；管理器不會執行原始 BAT／SH。
5. Windows 與 Linux argument file 不可混用。每個 `@argfile` 都必須是相對路徑、正規化後仍位於所選 Server 根目錄內，並於啟動前完成檢查。
6. 保存的設定不得含有 `Process`、PID、stream、task 或 cancellation 物件。
7. 下載檔在大小與密碼學雜湊驗證成功前維持 `.partial` 狀態。
8. 此個人版本依使用者明確指定的持續偏好，在每次啟動時自動確保 `eula=true`，不再顯示逐台同意視窗；匯入時不修改 EULA，判定與實際寫入只會在取得該 Server 根目錄的獨占執行鎖後進行。已接受的文件完全不改寫，需變更時保留編碼並採非覆寫備份、flush 與原子替換。
9. 此 MVP 的 GUI 不可與子 Server 分離；真正的 detach 需要獨立 host/service。
10. Port 選擇不保存遞增狀態：每次配置都結合目前 OS 的 TCP listener／connection、UDP listener，以及其他啟動中、執行中或等待啟動 Instance 的暫時保留值；停止中的 Instance 不永久保留 Port。衝突只會變更目標 Instance，不會停止已占用該 Port 的程序。
11. `server-port` 更新只針對呼叫端明確指定的一個 `server.properties` 路徑，且啟動路徑必須先持有 Server 根目錄的跨程序獨占鎖。既有內容會建立不覆蓋舊檔的 `.bak`／`.bak.N`，再寫入同資料夾暫存檔、flush 並替換；`query.port`、`rcon.port`、其他無關屬性及註解中的 Port 範例保持不變。
12. 玩家登錄 JSON 只供讀取顯示，不由 GUI 直接編輯。管理操作會先驗證、依所選 Instance ID 傳送，且 Server 未完全進入 `Running` 時一律拒絕；預設可見清單只含該 Session 的即時線上集合。標準輸出／錯誤流的登入離線事件先以 thread-safe 的 Instance→CoreType 快照選擇 Minecraft、Velocity 或 BungeeCord／Waterfall adapter，再直接更新每個 Instance／目前 Session 的 thread-safe、4,096 人有界權威集合。WPF 對每個 Instance 最多維持一個 Background snapshot drain，重新驗證 Instance、Session 與狀態後才套用；狀態切換另以 Send 優先清理，因此 flood 不會壓過輸入／Render，且絕不主動傳送 `list`。只有事件來源且未存在玩家登錄 JSON 的列會在離線時移除，不成為永久歷史資料。
13. 控制台自動跟隨是每個可見面板各自擁有的呈現狀態，不會改變有界 Runtime 紀錄或指令路由；向上捲動只會暫停該面板的尾端跟隨。
14. 匯入外觀時會把圖片複製到管理器擁有的 `themes` 子樹。清除外觀只能刪除通過路徑驗證的受管理副本，不得修改使用者原始圖片。
15. 同一 Server 根目錄只能有一個執行中的 Java Session。`.minecraft-server-manager.lock` 由 ProcessSession 全生命週期持有，不能因 Port 可重新配置而繞過；備份永遠排除此協調檔。
16. 可執行 JAR 必須存在於 Server 工作目錄內，且完整路徑不得經過 reparse point。驗證完成後只把相對於該工作目錄的路徑交給 `java -jar`；這同時保留路徑隔離，並避免舊版 Windows Java／Paperclip 無法載入非 ASCII 絕對 agent 路徑。
17. stdout 與 stderr 以原始位元組分流、逐行及有界緩衝；每行先嘗試嚴格 UTF-8，失敗時才使用 Windows 實際 ANSI code page。進入 WPF 前另有每 Instance 的 drop-oldest 有界佇列與固定批次 drain，不能讓 Java 輸出速度建立無上限 Dispatcher 工作。不得用固定 CP950 破壞現代 Java UTF-8，也不得藉由注入 `-Dfile.encoding` 改變 Server 或舊插件的檔案語意。
18. 全域外觀只能透過已驗證的 `ApplicationAppearanceSettings` 更新 WPF 動態資源。Window、Panel、Raised、Border、Accent、AccentDark、Text、Muted 與圖案色只接受 `#RRGGBB`；圖案限 None、Dots、Grid、Diagonal，透明度必須位於服務定義的有界範圍。所有自訂對話框都必須明確套用 AppWindowStyle、WindowBrush 與 TextBrush，不能依賴自訂 Window 型別無法繼承的隱含原生樣式。
19. 外觀預覽不可直接代表持久化：開啟編輯器時先保留原始快照；取消或關閉必須重新套用原始資源並清除未提交的受管理副本；儲存必須先完成驗證與圖片解碼，再以既有 JSON 原子儲存流程提交。恢復預設只改變工作副本，除非使用者再按儲存。
20. 全域背景圖片必須是實際可解碼的允許格式，檔案不得超過 64 MB、解碼結果不得超過 64,000,000 pixels，來源檔本身不得是 reparse point，`themes` 根目錄及受管理目的地不得是或經過 reparse point。應用程式只保存 `themes` 內的受管理副本路徑，不修改使用者原圖；取消未提交圖片或替換已提交圖片時，只能刪除再次通過受管理根目錄驗證的副本。
21. `manager.json` schema 3 引入 `Appearance`，schema 4 引入可靠性設定，schema 5 引入 nullable `SeparateDiagnosticOutput`；三者都是向後相容擴充。讀取 schema 1–4、缺少欄位、無效色碼、超界透明度或不可再驗證的背景路徑時，不得丟失既有 Instance；外觀服務逐欄修復為安全預設值，Hang Watchdog／自動健康恢復點採 opt-in 關閉，舊 Server 的 diagnostic 缺值／`null` 以 false 呈現且不在載入時改寫；只有新建與匯入流程會將缺值預設為 true。下一次明確儲存時寫回 schema 5。
22. 線上模組包安裝必須在 `servers/.installing-*` 的管理器擁有 staging 完成。下載、archive entries、hash、Pack／Version 身分、Minecraft／Loader 版本及標準啟動結構全部驗證成功後，才可用單次目錄搬移提交並加入 `manager.json`；取消或失敗不得註冊半成品。
23. 線上模組包 production surface 只允許 FTB 與 Modrinth，兩者不需要 API Key。ViewModel 不得接受其公開 `Providers` 集合外的 provider；workflow 的搜尋、推薦、版本與安裝公開入口收到 CurseForge 必須 `NotSupported`。正式 UI、鍵盤、Automation 與診斷不得顯示 CurseForge／API Key，Core 低階相容 Provider 不得繞過此 application boundary。
24. Pack 自帶的 BAT／SH／PS1、自訂安裝腳本及 JAR wrapper 一律不執行。線上流程可執行的外部程式只限管理器從固定官方來源下載並驗證的 FTB Server Installer 或 Mojang／Fabric／Forge／NeoForge Loader Installer，而且必須以 `UseShellExecute=false`、固定 executable 與 `ArgumentList` 直接啟動。Quilt 因無法強制建立相同的成功證據而在 0.3.0 fail closed。
25. Crash 與 Hang 不可混為單一猜測。Crash 由目前 Java Session 的非正常程序退出觸發；Hang 只在使用者啟用後，以 Minecraft status protocol、啟動寬限及連續失敗門檻判定，`enable-status=false` 時停用該 Session 的探測，絕不改用 `list`。Watchdog 必須先送 `stop`，等待 30 秒，逾時才 kill Process Tree，並將實際 stop mode 寫入診斷。
26. 所有 Crash／Hang 自動重啟都受每 Instance 的 10 分鐘視窗限制：三次允許的延遲依序為 5／15／45 秒，第四次開啟 circuit breaker；穩定運行 10 分鐘重設。Generation、Session ID、live policy 與手動停止 epoch 必須在延遲後及真正啟動前再次驗證，舊 Session 不得重啟新 Session 或造成重複 Faulted 事件。
27. 健康恢復點必須與 Start／Stop／Watchdog 共用每 Instance 協調，不得在生命週期切換時建立 ZIP。對 Running Session 先 `save-off`、`save-all flush` 並收到完成證據後才備份；任何後續失敗都必須嘗試 `save-on`。Session 在壓縮期間改變時，ZIP 不得宣告一致或成為可用恢復點。
28. 還原只能將通過 entry 數量、大小、壓縮比、路徑、特殊檔案與 reparse-point 驗證的 ZIP 提交到明確 `TrustedDestinationRoot` 下的全新資料夾；不得覆寫原始 Server。右鍵「從管理清單移除」同樣只修改管理清單，不刪除任何 Server 資料；右鍵「完全刪除 Server」則是另一條需停止、二次確認、確認前 identity lease、危險根／系統子樹／管理路徑重疊拒絕、handle-based no-follow 刪除，且原身分目錄成功消失後才移除管理記錄的 workflow。
29. 統一匯入按鈕只能選擇已有資料夾或單一 JAR 的安全流程；對話框不得自行執行 JAR、script 或繞過原本的靜態辨識與使用者確認。選擇視窗預設 700×500、最小 640×470 且可縮放；兩張卡片必須共用 58／自動延伸／72 欄位與 112 最小高度，在最小視窗仍完整且對齊。WPF owner 只能在擁有它的 Dispatcher／STA 上使用；跨 STA 呼叫必須安全省略 owner，不能因 UI affinity 異常終止應用程式。
30. 核心建立器的 UI 只顯示 production composition 實際回傳的 product／version，不內建假版本。建立前必須重新解析 canonical product／version／build；所有輸出先位於 `servers/.core-installing-*`，只有來源、大小、雜湊、核心種類、Minecraft 版本與啟動結構全部相符才能以單次目錄搬移提交。
31. 直接下載核心必須使用第一方 metadata 提供的強雜湊與大小證據；歷史 GitHub 資產只能在 release ID、tag、asset ID、檔名、大小、URL 與維護端 SHA-256 全部符合 allowlist 時使用。負責 Forge／NeoForge／Fabric 的 installer 也必須驗證後才可以固定引數直接執行。
32. Spigot／CraftBukkit 不得下載或散布第三方預編譯 JAR。建立前必須 fresh 讀 alias 與其不可變數字 identity JSON，逐欄比對名稱、四 refs、兩 hashes、工具版與原始 Java 規則，再以數字 identity 作 BuildTools `--rev`。BuildTools 必須使用受管理完整 JDK 及固定 SHA-256／版本驗證的 MinGit；JDK 必須同時存在 `java.exe` 與 `javac.exe`，只有已驗證 JDK／Git 路徑可前置到隔離子程序 PATH，Maven home 及工作目錄必須屬於本次 operation。私有 Git config 與 repository 必須採 `core.autocrlf=input`，Java／Maven 使用真 LF `line.separator`，完成後以受管理 Git post-check 四個 HEAD，最後硬性比對官方 output SHA-256；不得回退到 BuildTools 下載的 PortableGit SFX或放寬任何驗證。
33. WPF 顯示模板若把唯讀 CLR 屬性綁定到 `BindsTwoWayByDefault` 的 dependency property，必須明確指定 `Mode=OneWay`／`OneTime`。這不只包含 `ProgressBar.Value`，也包含 `Run.Text`；契約測試必須掃描 XAML，STA layout 測試則必須實際產生至少一個版本項目容器並完成 measure／arrange／layout，不能以空清單或單純 XAML parse 代表安全。

## 0.4.1 WPF layout 與線上目錄狀態

0.4.1 的事故基線包含七筆現場 `.NET Runtime` 1026：0.3.0 線上模組包視窗三筆因 `ProgressBar.Value` 預設 TwoWay 寫回唯讀 `ProgressPercentage` 終止；0.4.0 核心建立器另有四筆獨立事件，皆因版本 DataTemplate 中的 `Run.Text` 預設 TwoWay 寫回唯讀 `CoreServerVersion.BuildDisplay` 終止。兩類事故都發生在 WPF binding attach／layout，而非下載、核心 Provider、憑證、簽章或 Code Integrity。修正的架構規則是對所有這類唯讀顯示 binding 明確標示 OneWay，並讓 STA 回歸真正 materialize 版本列。

0.4.1 當時的線上模組包視窗已將「目錄作業」與「安裝作業」分開，並以 generation 加 cancellation token 使來源切換或取消後的舊結果失效。當時 FTB 與 Modrinth 會在視窗初次顯示或切換來源時自動呼叫 `GetFeaturedAsync`，使用者搜尋則呼叫 `SearchAsync`；當時的 CurseForge BYOK 行為屬於歷史設計，已由 0.4.4 production surface 移除。

推薦、搜尋與版本載入沒有可信總工作量，因此 ViewModel 只宣告 `IsProgressIndeterminate=true`，同時令百分比不可見；安裝 pipeline 報告有界比例時才顯示數字。UI 不得用 `0%` 代表未知網路進度，也不得用空清單區分「尚未查詢」、「查無結果」與「請求失敗」，這些狀態各自有明確文字。

## 0.4.0 統一匯入與核心建立信任邊界

`ExistingServerImportChoiceDialog` 是呈現層路由器，不改變原本的兩種 application workflow。資料夾匯入繼續靜態讀取 OS-specific Server Pack 版面，單一 JAR 繼續由 `JarCoreDetector` 辨識；兩者都不執行使用者提供的檔案。視窗以 700×500、最小 640×470 與 resize grip 提供足夠版面；兩個按鈕共用深色 control template、112 最小高度，以及一致的圖示／文字／尾端欄位，避免不同內建 Button template padding 造成錯位或裁切。對話框 service 不假定呼叫者與 MainWindow 必然共用 Dispatcher，避免診斷 STA 或測試 STA 將其他 thread 所擁有的 Window 當成 owner。

`CoreServerCreationWorkflow` 將 UI 與上游差異分開。`CompositeCoreServerCreationBackend` 以固定 product 順序整合三類 backend：Official 處理 Paper、Velocity、Vanilla、Fabric、Forge、NeoForge；Hybrid 處理 Mohist、Arclight、CatServer、Akarin；Spigot 處理 Spigot 與 CraftBukkit。UI 只顯示各 backend 目前能產生強驗證計畫的版本。Vanilla 雖允許 `1.0.0`–`26.2` 版本語法，目前 Mojang manifest 實際有 Server JAR 的範圍為 `1.2.5`–`26.2`；沒有 Server artifact 的更早版本不補假檔。

建立流程在下載前與下載後都有信任關卡。前者將 UI 選擇重新解析為當下上游的 canonical record，防止 stale／偽造 model 導向其他 URL；後者重新檢查雜湊、大小、JAR 格式、精確 CoreType 與版本證據。Paper／Mojang／Spigot／Hybrid 已驗證 artifact 的固定 hash／size 本身是版本身分證據，可支援沒有現代 `version.json` 的歷史 JAR；仍然必須是可讀 JAR 且核心 marker 符合，不是全局降低靜態辨識門檻。Forge／NeoForge 繼續從 argument files 比對精確 Minecraft／loader 版本。

Spigot BuildTools 是特殊的本機產生邊界：管理器只下載已固定的官方 BuildTools，不從鏡像取得核心。0.4.0 實作快照中只有 `26.2`、`26.1.2`、`26.1.1`、`26.1`、`1.21.11`–`1.21.4` 這 12 版同時提供 Spigot 與 CraftBukkit output SHA-256；目錄仍是即時讀取，上游證據改變時顯示數量可改變。舊 CatServer／Akarin 資產則不依賴可變的最新 release，而是逐個鎖定官方 release 與 asset identity 及已複核 SHA-256。

Paper 的上游政策要求 User-Agent 包含可識別且真實的聯絡方式。0.4.1 開發樹的核心建立字串仍是 `contact: Muhun`，不是可公開聯絡的 URL／電子郵件；不能以虛構 URL 解決。若要對外公開散布，維護者仍應提供真實專案網址或支援信箱並更新 User-Agent；目前本機自用版本會保留這項明確限制。

## 0.2.4 全域外觀資源與 UI 邊界

主視窗右上角齒輪建立 `AppearanceSettingsViewModel` 的交易工作階段。色彩及背景不直接寫死在單一視窗，而是由 `AppearanceThemeService` 驗證後更新 Application `ResourceDictionary` 中的動態 Brush；主視窗與匯入 Server Pack、匯入 JAR、Paper 版本及外觀設定對話框因此共用同一份即時預覽。None、Dots、Grid、Diagonal 圖案由程式產生，不需要載入外部樣板檔。

預設深色色盤與對話框已透過 Windows UI 實際畫面檢查文字可讀性、捲動區域、按鈕入口、預覽及取消還原；這項 QA 驗證的是內建預設值與指定互動流程。色碼允許使用者自由修改，因此架構不宣稱任何自訂前景／背景組合必定符合 WCAG；使用者可用「恢復預設深色」回到已驗證基準。

## 0.3.0 線上模組包信任邊界

0.3.0 的 `OnlineModpackDialog` 曾負責 provider 選擇、搜尋、版本、短生命週期憑證、進度與取消；當時 `IOnlineModpackWorkflow` 統一 FTB、CurseForge 與 Modrinth，並以各來源證據驗證 Installer／Pack。這一段只保留歷史信任邊界；0.4.4 的 application／UI 契約已由上方章節取代，正式功能只包含 FTB 與 Modrinth。

安全邊界不是「完全不執行任何程式」，而是「不執行 Pack 作者放入 archive 的任意程式」。必要的 FTB／Loader Installer 由管理器從指定官方來源取得、驗證、以固定 argument list 在 staging 中直接執行；Pack 自帶 script／wrapper 只作靜態資料或直接忽略。若版本沒有來源官方 Server Pack／`.mrpack`、散布政策不允許下載、Quilt 缺少可強制核對的成功證據，或安裝後找不到符合 Minecraft／Loader 身分的標準啟動結構，流程即拒絕提交。

## 0.3.0 可靠性狀態機

正常程序退出、非正常 Crash、程序仍存在但 status protocol 連續失敗的 Hang、手動 Stop 與應用程式 Shutdown 都是不同事件。Crash 使用 Core ProcessSession 的 Session ID 與退出狀態；Hang Watchdog 使用 App 內的 Session-scoped 狀態機，達門檻後進入安全 stop → 最多 30 秒等待 → 必要時 kill tree。兩條路徑共用 `CrashRestartLimiter` 與 per-instance lifecycle gate，延遲重啟前後都會重新驗證 Session、政策及手動停止 epoch。

崩潰診斷與資料復原刻意分離：診斷保存有界且遮罩秘密的報告，提供可解釋建議但不自動修改模組／世界；健康恢復點只在可確認 flush 的 Running Session 建立。還原由 `BackupRestoreService` 寫入同 parent staging，完整驗證後才提交到 `servers` 信任根內的新副本，原 Server 保持不變。

## OS 與 Server Pack 邊界

`SystemHostPlatformProbe` 會偵測目前執行 Core 的主機，只選擇相符的已安裝 Server Pack 版面：Windows 使用 `run.bat`／`win_args.txt`，Linux 使用 `run.sh`／`unix_args.txt`。能辨識兩種版面不代表目前應用程式能在兩個系統上顯示 GUI；桌面專案採 WPF，目前只發佈 Windows x64 EXE。Linux 路徑現階段涵蓋 Core 偵測與啟動定義測試，尚不是已發佈的 Linux 管理器。

## 下一個架構階段

加入 Windows Service host，並透過本機已驗證的 named pipe 通訊。Service 負責 Java 程序與紀錄，WPF 應用程式則成為可重新連線的 client。線上流程已有受控的官方 Forge／NeoForge Installer Provider；本機資料夾匯入仍只接受已完成安裝的 `win_args.txt` 版面，不會執行使用者提供的 installer。未來可由 Avalonia 或 daemon client 提供已測試的 Linux 啟動版面路徑，而不讓 Core 耦合至 WPF。
