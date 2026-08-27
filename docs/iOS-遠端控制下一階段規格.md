# iOS 遠端控制下一階段規格

## 建議結論

第一階段建議把現有手機 Web 控制台做成完整 PWA：iPhone／iPad 從 Safari 選擇「加入主畫面」後，會像 App 一樣從圖示全螢幕開啟，可保存安全登入工作階段，不需 App Store、Apple Developer 會員、IPA、描述檔或每 7／90 天重裝。第二階段只有在確定需要 Face ID、Keychain、APNs 或更深的原生整合時，再建立 SwiftUI App。

「從網站下載 IPA，再安裝描述檔給任何 iPhone 使用」在台灣的一般個人帳號下不可行。`.mobileconfig` 不能繞過 Apple 程式碼簽署與 provisioning；真正的原生選擇是 TestFlight，或只供預先登記 UDID 裝置使用的 Ad Hoc。

## 可選發行方式

| 方式 | 使用者體驗 | 限制 | 建議 |
|---|---|---|---|
| PWA／加入主畫面 | 有主畫面圖示、standalone 介面、自動登入 | 部分原生 API 與背景能力受限 | 第一階段首選 |
| TestFlight | 真正 SwiftUI App，可用 Keychain／Face ID／APNs | 需 Apple Developer Program；每個 build 90 天 | 原生測試首選 |
| Ad Hoc 網頁安裝 | 不經 App Store，下載已簽署 IPA | 每台 iPhone UDID 必須預先登記；裝置名額與 profile 到期 | 少量固定管理員 |
| 免費 Personal Team | 可裝到自己的測試機 | 7 天到期、裝置與 App 數量很少 | 只適合短期開發測試 |

Enterprise 只允許合資格組織分發給自己的員工；EU Web Distribution 也有地區、組織與 Apple 核准條件，不能當作台灣個人的一般網站安裝方案。

## MCSV 專用架構

1. App／PWA 第一次用帳號與 PIN 登入後，由 MCSV 簽發可撤銷的 refresh token；不要長期保存原始 PIN。
2. 原生 App 把 token 放入 iOS Keychain，PWA 使用 Secure、HttpOnly、SameSite cookie；電腦端刪除帳號、重設 PIN、變更權限或「登出所有手機」時立即撤銷 token。
3. 前景使用 HTTPS API 與 WSS 即時控制台；App 回到前景時重新驗證、重連並補抓狀態。
4. iOS 背景不能保證 WebSocket 永久存活。重要錯誤通知後續用 APNs／Web Push；背景工作只做短時間同步。
5. 保留目前逐帳號六項權限，所有 API 在後端再次驗證，不能只靠按鈕隱藏。

## 固定入口是必要條件

Cloudflare Quick Tunnel 每次重新建立都可能換 `trycloudflare.com` 網址。即使 App 保存了帳號或 token，若不知道新的伺服器網址仍無法自動登入；隨機網址也不是安全驗證。

正式使用建議二選一：

- 使用 Cloudflare Named Tunnel 與自有固定網域，繼續依靠 MCSV 帳號、權限與撤銷機制保護操作。
- 保留隨機 Tunnel，但建立獨立、經驗證且只回傳目前端點的配對／發現服務；App 以裝置金鑰取得最新地址。這條路需要額外公開服務與維運，複雜度較高。

對目前專案，Named Tunnel＋固定網域是較安全、簡單且容易讓 PWA／iOS 自動登入的方案。

## Apple 官方參考

- [Apple Developer 會員方案比較](https://developer.apple.com/support/compare-memberships/)
- [TestFlight 概覽](https://developer.apple.com/help/app-store-connect/test-a-beta-version/testflight-overview)
- [分發到已登記裝置（Ad Hoc）](https://developer.apple.com/documentation/xcode/distributing-your-app-to-registered-devices)
- [Provisioning profile 到期說明](https://developer.apple.com/documentation/technotes/tn3125-inside-code-signing-provisioning-profiles)
- [Apple Developer Enterprise Program](https://developer.apple.com/programs/enterprise/)
- [EU Web Distribution](https://developer.apple.com/support/web-distribution-eu/)
- [Keychain Services](https://developer.apple.com/documentation/security/keychain-services)
- [App Transport Security](https://developer.apple.com/documentation/security/preventing-insecure-network-connections)
- [iOS 背景執行時間](https://developer.apple.com/documentation/uikit/extending-your-app-s-background-execution-time)
- [Web Push](https://developer.apple.com/documentation/usernotifications/sending-web-push-notifications-in-web-apps-and-browsers)
- [iPhone 將網站加入主畫面](https://support.apple.com/guide/iphone/iphea86e5236/ios)
