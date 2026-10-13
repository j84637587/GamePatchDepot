# 完整客戶端與 maplelink 整合提案

日期：2026-09-24。狀態：供確認的設計提案，尚未實作或部署。

2026-09-26 進度補充：管理端已先實作完整客戶端收錄、schema 2 建置與 UI 接線，詳見 [目前操作說明](FULL_CLIENT_PUBLISHING.md)。下方是原始整體提案，包含仍未完成的分塊量測、獨立前綴與 maplelink 串接；請以追蹤清單判斷實際完成狀態。

實作進度與目前下一步：[共用追蹤清單](FULL_CLIENT_TRACKER.md)。

## 一頁決策

建議保留兩個專案，以一份有簽章的完整版本清單串接。GamePatchDepot 是管理員的發布工具；maplelink（目前產品名稱 NovaLink）是玩家的下載與啟動工具。R2 負責提供檔案，不需要為下載另外租一臺伺服器。

```mermaid
flowchart LR
    A[乾淨的完整客戶端目錄] --> B[GamePatchDepot\n建立、驗證、發布]
    B --> C[R2 與自訂下載網域\n簽章清單與共用分塊]
    C --> D[maplelink\n安裝、更新、修復]
    D --> E[本機完整客戶端]
    E --> F[遊戲伺服器]
```

| 項目 | 第一版建議 |
|---|---|
| 管理內容 | 整個客戶端的 EXE、DLL、NX 與必要子目錄 |
| 版本 | 整個客戶端一個 Content Version，每個檔案以 SHA-256 識別內容 |
| 更新形式 | 完整清單＋只下載缺少的分塊，首次安裝與更新使用同一條流程 |
| 分塊 | 固定 8 MiB 作為測試起點；正式定案前比較實際 NX 更新樣本 |
| 發布 | 沿用 Prepare → Build → 驗證 → Upload → Test → Stable |
| 玩家操作 | 安裝／更新／開始遊戲，另有修復；顯示速度、剩餘容量、暫停與繼續 |
| 安裝中斷 | 已驗證分塊保留；套用中斷由交易紀錄恢復 |
| 退版 | 用舊內容建立更高版本的新 Release，所有玩家正常更新取得 |
| 雲端 | 沿用 R2 bucket，新內容隔離在 `client-v2/`；正式使用自訂網域 |
| 暫不加入 | 多個獨立資源版本、P2P、連續版本 patch 鏈、下載專用 API 或資料庫 |

這裡的「整個客戶端」指可以交給玩家執行的發行內容；帳密、日誌、截圖與個人設定不屬於發行內容。玩家取得更新後仍需實際遊戲測試，檔案驗證不能證明遊戲相容性。

## 1. 已檢查的現況

| 專案／程式位置 | 實際狀況 | 對整合的影響 |
|---|---|---|
| GamePatchDepot `config/allowed-files.json` | 16 個固定 NX | 需換成完整客戶端檔案政策 |
| `ReleaseBuilder.cs` | 明確禁止 Candidate 子目錄與非 NX；目前固定 256 MiB 分塊 | 不能只加 `.exe` 副檔名，必須連同路徑、列舉及驗證一起調整 |
| `CandidateAndRollback.cs` | Candidate 只複製變更檔，Manifest 合併基準內容 | 沿用差異 Candidate，但輸出的 Manifest 必須完整且能獨立安裝 |
| `Models.cs`、`CryptoAndJson.cs` | 檔案與分塊 SHA-256、ECDSA P-256 簽章、簽署的 Channel | 已具備跨語言協定基礎 |
| `GarbageCollector.cs` | 固定讀取根目錄 channels、history、objects | 新舊內容必須隔離，GC 要能限定自己的範圍 |
| maplelink `services/client_manager.rs` | 官方台版 Manifest、整檔下載、雜湊、暫停、重試、單檔驗證後替換 | 可沿用 UI 和控制流程概念；尚無我們需要的跨檔案交易與分塊重用 |
| 同檔 `fetch_one` | 每次重試重新下載該檔，取消時移除未完成檔 | 現有續傳是跳過已完成檔案，不是部分檔案的位元組續傳 |
| `services/game_download.rs` | 官方來源與 CDN 網域檢查 | 應新增自有產品來源，不能只替換 URL 或取消所有來源限制 |
| `services/update_service.rs`、`update_signature.rs` | 啟動器自行從 GitHub 更新，使用 minisign 驗章 | 啟動器自我更新與遊戲更新應分開，兩種簽章格式也不同 |
| `EMULATOR_MIGRATION_CHECKLIST.md` | 已有專用 Emulator 啟動器改造規劃 | 本提案承接其下載與更新部分；登入／註冊／票證另外實作 |

本次只新增提案文件。maplelink 工作目錄目前有大量既有修改，實作前應先確認基準，避免覆蓋正在進行的 Emulator 改造。

## 2. 哪些做法值得參考

SteamPipe 使用分塊、內容重用及版本清單；其文件也提醒，大型封裝檔若重新排列內容，會放大更新下載量。我們採用同樣的基本方向，但只做一個產品與 Test／Stable 兩個頻道，不複製完整平台。[Steamworks 官方說明](https://partner.steamgames.com/doc/sdk/uploading)

Tauri 的更新工具處理的是啟動器本身，要求更新包簽章。遊戲檔案的安裝、修復、刪除與分塊仍需自己的邏輯。maplelink 現在已自行實作自我更新，不需要為這次整合先改寫成 Tauri updater。[Tauri 官方說明](https://v2.tauri.app/plugin/updater/)

以下的 8 MiB、交易目錄和保留策略，是依個人工作室維護成本提出的建議，並非宣稱其他公司的內部實作完全相同。

## 3. 檔案與版本規則

發布來源應是一個專用、乾淨的 `ClientRelease/` 目錄。以下檔名僅為示意；真正的 EXE、DLL 名稱與位置，要以可正常啟動的 GMS176 客戶端確認。

```text
ClientRelease/
  MapleStory.exe
  example.dll
  UI.nx
  Map.nx
  其他 NX…
  data/必要的資料檔
  defaults/預設設定範本
```

| 分類 | 發布與更新行為 | 例子 |
|---|---|---|
| 系統管理檔案 | 精確比對並更新；由版本管理 | EXE、DLL、NX、必要資料 |
| 玩家可修改內容 | 保留，不用修復功能覆蓋 | 畫面設定、快捷鍵、截圖、日誌 |
| 預設設定範本 | 範本可更新；只有第一次使用才建立玩家設定 | `defaults/settings.ini` → 本機使用者設定 |
| 排除檔案 | Prepare 不允許進入 Candidate | `.git`、私鑰、憑證、帳密、暫存及編譯中間檔 |

新增 `publish-rules.json`：列出必備檔案、核准收錄的相對路徑／子目錄，以及保留路徑。避免以 `**/*` 無條件收錄開發目錄。Prepare 顯示新增、修改、刪除及排除清單，新增 EXE／DLL 要在發布前預覽中明確顯示。

規則只允許發布來源內的檔案；拒絕路徑跳脫、磁碟絕對路徑、UNC、ADS、Windows 裝置名稱、尾端空白或點、大小寫衝突、檔案／目錄衝突與 junction／symlink。發布端及玩家端都檢查，包含父目錄與實際寫入時的連結狀態。

版本例子：`Release ID = 2026.09.25.1`，`Content Version = 4`。即使只改一個 DLL，也產生一個新的完整客戶端版本；其餘檔案沿用原 hash／分塊。不要讓 EXE、DLL、NX 各自自由選版本，否則容易組出沒有測試過的組合。

## 4. 下載與 patch 如何共用

每份 Manifest 都描述「這個版本完整應有的檔案」，而不是只列上次變更。

1. maplelink 取得並驗證 Stable 頻道與目標 Manifest。
2. 比對本機內容，計算缺少或損壞的分塊。
3. 可驗證重用的本機檔案區段先重用，其餘向 R2 下載。
4. 在暫存區組合檔案，檢查分塊與整檔 SHA-256。
5. 全部準備好後套用，最後提交本機版本。

因此新玩家下載全部缺少的內容；舊玩家下載差異；損壞檔案走同一流程修復。玩家從版本 1 直接更新到版本 10，不必依序下載 1→2、2→3 的補丁。第一版的 patch 就是差異分塊，無須另產生 patch.exe 或每版 ZIP。

本機保存已安裝的完整 Manifest，記住舊檔案的分塊位置。重用某個區段前重新計算 hash，不能只相信檔案大小或修改時間。EXE／DLL 在啟動前完整驗證；大型 NX 可先快速檢查，使用者按「修復」時做完整雜湊掃描。快速檢查不能證明所有內容都正確。

### 分塊大小建議

目前 256 MiB：一個區段改幾 KB，也可能要下載整個 256 MiB。先比較 4／8／16／256 MiB，建議從 8 MiB 固定分塊開始；小於 8 MiB 的檔案通常只有一塊。每個檔案獨立分塊，內容相同的塊只存一次。

固定分塊仍有侷限：如果 NX 重建時在前段插入資料，後面的邊界位移，可能整檔都變成新分塊。不要承諾「只改 1% 就只下載 1%」。若實際樣本下載量明顯過大，再評估內容定義分塊（CDC）；它也不能保證解決重新壓縮或大範圍重排。

量測至少包含只改 UI、一個大型 NX 小修改、EXE／DLL 更新及多檔案更新；記錄新分塊總量、Build 時間、玩家組檔時間、請求數與尖峰磁碟空間。調小分塊會造成一次性的重新分塊／重新上傳成本。

下載併發先設 4，最多 8；串流寫入磁碟、不把整個 NX 放進記憶體。第一版以「已驗證分塊」續傳；中斷中的單塊重新下載，之後有需求再加 HTTP Range。遇到 429／5xx 按退避時間重試，提供速度限制避免占滿玩家網路。

## 5. EXE／DLL 套用、取消與斷電恢復

下載完成不代表版本已安裝。不能每抓好一個檔案就立即換掉，否則中途可能出現新 EXE 配舊 DLL。

```text
檢查版本 → 下載缺少分塊 → 組檔與驗證 → 等待遊戲關閉
         → 建立交易紀錄 → 備份與替換 → 提交版本 → 清理暫存
```

在遊戲安裝磁碟上建立 `.patch/`，內含分塊快取、目標版本 staging、backup 和 journal。journal 保存交易 ID、固定的目標 Manifest hash、舊版本、每個檔案的原始／目標 hash、動作與進度。檔案在移動前記錄意圖，完成後記錄結果並落盤；重新啟動可依路徑與 hash 判斷尚未完成的動作。

- 開始套用前取得跨程序的安裝目錄鎖，禁止同時更新與經由啟動器啟動遊戲；確認該目錄遊戲程序已關閉，無法取得檔案時顯示等待或重試。
- 只備份本次會替換／移除的舊檔；同磁碟 rename 避免另複製一份完整舊客戶端。新檔先全部驗證完成。
- 多個檔案的替換不是單一原子操作。套用中途失敗或斷電，啟動器必須先按 journal 恢復或完成交易，才能提供「開始遊戲」。
- 全部檔案完成並驗證後，原子提交 `installed.json`（版本與 Manifest hash）。在這之前不能顯示更新成功，也不能刪除交易備份。
- 下載中可暫停／取消，保留已驗證的分塊；套用中不直接殺程序，取消請求進入可恢復的還原流程。斷電仍由 journal 接手。
- 成功提交後保留最近一次變更備份，到下一次更新開始前再清理；長期公開退版依 R2 上保留的 Release。

磁碟檢查要分開算「網路下載量」與「組檔所需容量」。保守估算新增可用空間 ≥ 尚需下載的分塊＋本次重建檔案完整大小＋安全餘裕；若暫存和目標不同磁碟則拒絕或重新配置。即使只下載 8 MiB，重建一個 10 GiB NX 仍可能需要約 10 GiB 額外空間。快取用完可回收，但不能刪除進行中交易仍需的內容。

遊戲更新工具不替換自己的啟動器 EXE。啟動器安裝目錄與遊戲目錄分開，自我更新沿用獨立流程。第三方執行階段若需要安裝，另列明確的安裝步驟，不把安裝程式當成普通檔案下載後自動執行。

## 6. 檔案刪除、玩家資料與退版

正常更新：以「已安裝的舊 Manifest」減去「目標 Manifest」得到已退役的管理檔案，先移入交易備份。這能涵蓋跳過多個版本的玩家，不依賴只記錄相鄰版本刪除項目的清單。

不在舊 Manifest 的檔案不自動刪除。對第一次接管的既有客戶端，要求使用者選擇／確認獨立 NovaStory 目錄；初次完整掃描後才建立受管狀態。不明 DLL／EXE 若可能影響啟動則提示移出或改用乾淨安裝。不能因清單沒有列出，就遞迴刪除玩家資料夾。

伺服器退版範例：Content 10 有問題，管理員以 Content 8 的完整內容建立 Content 11。客戶端仍往前更新到 11，重用已存在分塊。不支援把頻道版本直接降回 8；只允許選取遠端內容仍完整保留的退版目標。

舊 NX-only Manifest 不包含 EXE／DLL，因此不能直接當成完整客戶端退版基準。若只想恢復舊 NX，應先在完整客戶端來源中替換指定 NX，重新建立並測試完整 Release。

## 7. 兩個專案的共同協定

建立 schemaVersion 2，明確定義完整客戶端語意。下面是欄位約定，實作前另產生正式 JSON Schema 與跨語言測試資料；目前不是可直接拿去發布的設定。

| 資料 | 必要內容 |
|---|---|
| Channel payload | product、channel、generation、releaseId、contentVersion、manifestKey、manifestSha256、publishedAt |
| Manifest | schemaVersion、product、gameVersion、releaseId、contentVersion、minimumLauncherVersion、createdAt、entryPoint、files |
| 每個檔案 | 相對 path、size、sha256、依序排列的 chunks |
| 每個分塊 | offset、size、sha256、限定前綴內的 key |
| 本機狀態 | product、安裝目錄、已安裝版本、完整 Manifest、每頻道已見最高 generation、未完成交易 |

簽章沿用發布端 ECDSA P-256／SHA-256，公鑰隨啟動器發布；私鑰和 R2 API 憑證只留在發布端。Channel envelope 使用原本的 Base64 payload；Manifest 驗證下載的原始位元組，不重新序列化 JSON 後再驗章。

現有 .NET `SignData` 呼叫沒有指定簽章編碼；跨語言實作時要明確固定為 IEEE P1363 的 64-byte r||s，並以 .NET 產生、Rust 驗證的固定測試向量驗收。它不是 maplelink 自我更新使用的 minisign，兩組驗章流程與金鑰分開。

讀取順序：驗 Channel 簽章與產品／頻道 → 驗版本、generation → 下載 Manifest 與 detached signature → 驗 hash、簽章、schema、啟動器最低版本 → 驗路徑與分塊結構 → 下載內容。需檢查 offset 連續、總長度、大小上限、重複路徑、雜湊格式及零位元組檔案規則；拒絕未知 schema。

自有內容只從設定好的 HTTPS 主機與前綴取得；拒絕跳轉到未核准主機、禁止關閉 TLS 憑證驗證。前端不能任意指定下載 URL 或待刪除路徑，Rust 根據已驗證的安裝計畫決定。

`minimumLauncherVersion` 不能沿用現在發布端預設 1.0.0 就不管；目前 maplelink 是 0.6.1。實作時先發布支援 v2 的啟動器版本，再讓 Manifest 填入那個實際版本。

第一版以保存最高 generation／Content Version 防止已安裝客戶端被降版；對全新安裝遭重播舊而有效的簽章，只有本機紀錄並不足夠。第一版接受這項限制並依賴正確 HTTPS 來源；如要嚴格防止長期凍結，再加入會定期重簽的到期 metadata／可信時間機制，不能宣稱僅靠簽章已完全解決。

## 8. R2 結構、快取與容量

建議新完整客戶端先使用獨立前綴，降低舊 GC 誤刪新內容的風險：

```text
novastory/
  channels/、manifests/、objects/、channel-history/  ← 現有 NX 發布
  client-v2/
    channels/test.json、stable.json               ← 已簽章的目前版本指標
    manifests/<release>.json、<release>.sig       ← 完整且不可變
    objects/sha256/<前兩碼>/<hash>                ← 不可變內容分塊
    channel-history/test/、stable/                ← 已簽章發布歷史
```

發布端增加前綴／產品設定並隔離本機 catalog、Candidate、報告與操作鎖；本機目錄可用 `work/client-v2/`、`catalog/client-v2/`。完整客戶端的所有遠端讀寫、驗證、GC 和鎖都必須涵蓋前綴，不能只在上傳路徑加上去。

新舊前綴初期不跨區重用分塊，因此會暫時增加儲存；這是避免交叉 GC 的簡化取捨。切換 8 MiB 本來也會使多數舊 256 MiB 分塊無法直接重用。遷移成功且確認不再需要 NX 發布後，才能獨立盤點舊區清理。

正式下載使用 R2 Custom Domain，例如 `download.你的網域`。`r2.dev` 有變動式限流，官方只建議開發用途；自訂網域能配合 Cloudflare Cache。[公開 bucket 官方說明](https://developers.cloudflare.com/r2/buckets/public-buckets/)

設定明確的 Cache Rules：objects／不可變 manifests 可快取一年；channels 必須略過快取或每次重新驗證，不能被長效快取規則覆蓋。對沒有副檔名的 hash objects 也要設置快取資格，不能假設預設會快取。驗收要檢查實際回應 header、命中與版本切換時間。[R2 與快取官方說明](https://developers.cloudflare.com/cache/interaction-cloudflare-products/r2/)

台灣玩家速度需以實際 ISP 測試冷／熱快取、下載速度與重試率；CDN 和免費流出不代表保證頻寬。遊戲連線與檔案下載分離後，遊戲 VPS 不承擔整包下載流量。

保留目前 Stable、目前 Test、最近 3 個正式版本與管理員明確固定的救援版；沒有被這些完整 Manifest 引用的塊，經 14 天寬限再 GC。先產報告，再重新核對保留集合與頻道 generation 才刪除。14 天由「不再被保留版本引用」起算，不能只用物件原始建立時間冒充退役時間；需補上退役觀測紀錄。

下載中的玩家固定使用同一份目標 Manifest；超過保留時間遇到 404，重新規劃目前 Stable，重用快取。不要混用新舊 Manifest 組合內容。

### 費用試算

R2 Standard 公開定價為 US$0.015／GB-month，Class A US$4.50／百萬次、Class B US$0.36／百萬次；免費額度為 10 GB-month、100 萬次 A、1,000 萬次 B，直接流出免流量費。額度以帳戶合計，計費有進位規則。[R2 官方定價](https://developers.cloudflare.com/r2/pricing/)

假設整月每日尖峰儲存的平均值為 100 GB、免費額度尚未被其他用途消耗，儲存約 US$1.35／月；300 GB 約 US$4.35／月。只為預算換算，若以 US$1＝NT$33 計，約 NT$45／144；這不是即時匯率，亦未包含網域、稅金與其他服務。

假設 30 GiB 客戶端，大檔以 8 MiB 分塊約 3,840 塊，很多小檔會增加數量。1,000 次全新下載約 384 萬次 object GET，未算重試／metadata，且實際到 R2 的請求會受 CDN 命中影響。即使月計費 Class B 到 2,000 萬次，扣除免費額度後約 US$3.60；更新請求與帳戶其他用途仍需一併觀察。

因此在先前 NT$2,000／月整體預算裡，檔案儲存通常不是主要成本；建議初期為這部分先抓 NT$100～300 的觀察額度，設定用量提醒（提醒不是硬性停費）。主要取捨是開發與維護正確更新流程的時間。

## 9. 實作順序與驗收

| 階段 | 工作 | 完成的證據 |
|---|---|---|
| A：協定與樣本 | 確認真正完整客戶端、路徑政策、v2 schema、簽章測試向量；量測分塊 | C# 產生的資料由 Rust 驗章成功；竄改必須失敗；有分塊比較報告 |
| B：發布端 | 支援子目錄、EXE／DLL、完整 Manifest、差異 Candidate、刪除預覽與新前綴 | 測試客戶端從零發布到 Test；只改一 DLL 時不複製其他檔；必備檔缺漏拒絕發布 |
| C：玩家下載 | 接入 NovaStory 來源、首次安裝、區段重用、修復、分塊續傳 | 空目錄安裝、跳版更新、毀損修復與中斷重開均成功，下載量可解釋 |
| D：套用交易 | staging、目錄鎖、journal、備份、復原、版本提交 | 在每個替換階段中斷後重開，都能恢復一致版本；EXE 鎖定／磁碟不足不顯示成功 |
| E：上線演練 | 自訂網域、快取、退版、GC、舊資料保護、實際 GMS176 啟動 | 台灣網路測速、v1→v3→新退版演練；清理後保留版本仍可從零安裝 |

GamePatchDepot 預計修改：`Models.cs`、`ProjectContext.cs`、`CandidateAndRollback.cs`、`ReleaseBuilder.cs`、`ReleaseCatalog.cs`、`RemoteReleaseService.cs`、`GarbageCollector.cs` 及 PatchManager UI。既有只收 NX 的假設與提示全部改成完整客戶端語意。

maplelink 預計新增：`models/game_patch.rs`、`services/game_patch_service.rs`、`services/game_patch_transaction.rs` 及對應 commands；沿用 `features/client` 的下載進度與修復介面。在可驗收的整合點抽出可共用邏輯，不先全面重構官方下載器。

啟動器自我更新需另外確認發布 repository、產品識別與簽章公鑰，使專用 NovaStory 版本不會被目前通用 NovaLink release 覆蓋。保留現有更新機制，但不能直接沿用錯誤的發布目的地。

開發工作量粗估：熟悉兩邊程式的一人約 10～18 個工作天，包含 Windows 異常恢復與實際客戶端測試；不含登入 API、註冊、伺服器搬遷與程式碼簽章憑證申請。這是規劃估算，不是已測得的交付期限。

## 10. 日常 SOP

管理員：把可玩的完整客戶端準備好 → 在 Patch Manager 選來源 → 查看新增／修改／移除清單 → 建立與驗證 → 上傳 → 遠端深度驗證 → 發布 Test → 測試安裝、更新、啟動、登入及地圖 → 發布 Stable。

玩家：打開 maplelink → 自動檢查 → 首次選資料夾後按「安裝」，已有版本按「更新」 → 看進度或暫停 → 完成後按「開始遊戲」。發生毀損時按「修復」。初次安裝前顯示下載量與磁碟需求；更新中的遊戲關閉提示和恢復狀態要用一般人看得懂的文字。

發布錯誤：在管理工具選擇仍保留的完整舊版 → 建立更高版本的退版 → Test 驗證 → Stable。已發布內容與簽章不可原地覆寫。

開始實作前只需要補齊：真正完整客戶端所在目錄、遊戲主 EXE／啟動方式、哪些設定須保留，以及正式下載網域。前兩項影響安裝後能否啟動；網域可在本機開發期間再設定。
