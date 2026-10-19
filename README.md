# Game Patch Depot

Game Patch Depot 是 GMS v176 遊戲檔案的版本、分塊、Cloudflare R2 發布與清理工具。新版本可收錄完整客戶端的 EXE、DLL、NX 與子目錄；既有 NX 版本仍可讀取。

管理 UI `PatchManager` 與命令列 `PatchPublisher` 使用同一套發布核心。不需要資料庫、Web 後臺或 AWS CLI。

NovaLink 本身的更新請看 [NovaLink 發布 SOP](docs/LAUNCHER_PUBLISHING.md)，可使用 GitHub Actions 或「啟動器發布」分頁。先上傳並回驗，確認後才發布 Stable；遊戲與啟動器不混用版本或金鑰。

## 已完成能力

- 公告管理：草稿、預覽、發布、下架；R2 已簽章清單與歷史紀錄，與遊戲版本分開。

- Candidate 白名單驗證。
- 防止絕對路徑、`..`、junction 與 symlink。
- 固定 256 MiB 分塊。
- 變更檔案額外產生差異補丁，並保留完整分塊作為備援（玩家端待串接）。
- 檔案與 chunk SHA-256。
- 不可變 Release Manifest。
- ECDSA P-256 Manifest 與 Channel 簽章。
- 本機重組驗證。
- 版本差異與下載量報告。
- R2 S3 相容 API 去重上傳。
- 有上限的並行上傳，預設同時處理 4 個物件。
- R2 淺層與深度驗證。
- R2 深度驗證顯示下載位元組進度、目前檔案與容量；所有檢查及驗證紀錄寫入成功後才顯示 100%。
- `test`／`stable` 原子切換。
- 遠端操作鎖、續租及持有者驗證。
- 可重跑的 Promote 與 Channel history 補齊。
- GC Dry Run、generation 驗證、重新標記及安全刪除。
- 建立單調遞增的回滾版本。

完整規格請讀：[檔案儲存、版本管控與發布 SOP](docs/PATCH_STORAGE_ARCHITECTURE_AND_SOP.md)。

完整客戶端發布已支援；maplelink 玩家更新仍待串接：[完整客戶端操作說明](docs/FULL_CLIENT_PUBLISHING.md) · [進度追蹤與下一步](docs/FULL_CLIENT_TRACKER.md) · [架構提案](docs/FULL_CLIENT_MAPLELINK_PLAN.md)。

差異補丁的操作、空間需求與玩家端串接規則：[差異補丁說明](docs/DELTA_PATCHES.md)。

## 公告操作（NovaStory）

1. 開啟 Patch Manager 的「公告管理」，按「重新整理已發布公告」。
2. 按「新增公告」，填寫標題、內容及分類。第一版使用純文字，可換行，不執行 HTML 或腳本。
3. 需要玩家開啟啟動器時看到彈窗，勾選「開啟啟動器時彈出」。修改已發布公告時，只有勾選「修改後再次提醒」才會讓已讀玩家重新看到彈窗。
4. 「儲存草稿」只保存在管理電腦；「預覽」不會發布。確認後按「發布公告」，會直接提供給新版 NovaLink。
5. 要下架，選取已發布公告並按「下架公告」。保留歷史，不刪除發布紀錄；重新發布下架公告時，如需重新提醒請勾選再次提醒。

例如：發布「今晚 22:00 維護」，勾選彈出。之後只改錯字，不勾再次提醒；若維護時間改成 23:00，再勾再次提醒發布。

儲存位置：`work/announcements/draft.json` 為本機草稿；R2 `announcements/current.json` 為已簽章的最新清單，`announcements/history/{版次}-{SHA256}.json` 為不可修改的歷史快照。公告 ID 與版次自動產生，不占用遊戲 Release ID／Content Version。沿用既有遊戲 ECDSA 簽章金鑰，資料用途固定為 `NovaStoryAnnouncements`；不是啟動器的 Minisign 金鑰。請保護既有憑證與私鑰。

第一版最多 200 則公告（含下架紀錄），清單封套最多 1 MiB。取消、網路中斷或衝突後，請重新整理確認遠端結果，不要直接假設已撤銷發布。並行編輯會以版次及 ETag 條件寫入防止覆寫；公告歷史不由遊戲 GC 清除。

NovaLink 開啟時與公告歷史頁會取得最新清單；驗章失敗、清單倒退或離線時，保留最後一次驗章成功的暫存，無可用公告則不彈出，不阻止登入。下架在下一次成功讀取清單後生效；離線玩家可能仍看到之前的暫存。公告不等於維護登入限制，登入限制仍須由遊戲伺服器處理。

## 系統需求

- Windows 10／11 或 Windows Server。
- .NET 10 SDK。
- Cloudflare R2 Bucket。
- 已綁定 R2 的正式下載網域，例如 `patch.example.com`。

## 編譯與測試

```powershell
cd F:\MSPrivate\GamePatchDepot
.\setup-patch-tools.ps1
dotnet restore GamePatchDepot.slnx
dotnet build GamePatchDepot.slnx --configuration Release
dotnet test GamePatchDepot.slnx --configuration Release
```

執行程式：

```powershell
dotnet run --project src\PatchPublisher --configuration Release -- help
```

也可以使用專案根目錄的包裝指令：

```powershell
.\patch.ps1 help
.\patch.ps1 status
```

建立可直接執行的 Windows Release：

```powershell
.\build-release.ps1
```

輸出位置：

```text
dist\win-x64\PatchPublisher.exe
dist\win-x64\PatchManager.exe
```

## Windows 管理 UI

日常人工發布建議使用：

```powershell
.\dist\win-x64\PatchManager.exe
```

UI 提供：

- Test／Stable 頻道狀態。
- 自動保存目前版本、來源路徑與更新說明；關閉再開會回到最新的未完成版本。
- 「重設／下一版」產生新版本號，清空更新說明與選用替換檔、將進度歸零；保留來源與上傳設定，不刪本機或 R2 檔案，也不取消未完成版本。
- 已發布 Test／Stable 顯示完成標記；已發布版本不提供直接回退或取消。Stable 完成後保留畫面，按重設才開始下一版。
- Release ID 與 contentVersion 建議值。
- 完整客戶端目錄與選用的 `UI.nx` 替換檔選擇。
- Prepare、Build、本機驗證、Diff、Upload、R2 深度驗證。
- Test／Stable 發布與 Stable 二次確認。
- chunk 進度與完整操作日誌。
- 每個欄位的 `?` Hover 說明與獨立操作說明視窗。
- Release ID、Content Version、完整客戶端 EXE、設定的必備檔案、替換檔與流程順序防呆。
- Stable 必須和 Test 同版、完成當次深度驗證、勾選遊戲測試並輸入完整 Release ID。

UI 仍透過 `patch.ps1` 執行同一套發布核心；自動化、救援及除錯時繼續使用 CLI。

### 上傳速度與並行數

不用增加操作步驟，按 Upload 就會依 `config/product.json` 的 `uploadConcurrency` 同時處理多個分塊／補丁。預設為 `4`，允許 `1`～`8`；設為 `1` 就是依序上傳。修改後於下一次 Upload 生效，不改變已在執行的上傳。

UI 左側「發布流程」標題旁的「同時上傳」選單可直接調整，不必手改 JSON。選完自動儲存，重新開啟也會保留；操作執行中選單會停用。旁邊的 `?` 只有懸停說明，不會開啟說明彈窗。

先使用 `4`；若磁碟或網路負擔太大可降為 `2`，若上行頻寬仍有餘裕再比較 `8`。並行數不是保證倍速：總速度仍受上行頻寬、磁碟讀取、SHA-256 計算與遠端服務限制。物件以串流上傳，不會先把 4 個完整分塊全部讀進記憶體。單一物件不會再拆成多路上傳。

進度以完成或略過的物件數計算，不是已傳輸位元組百分比。操作鎖由單一背景工作續租；任一物件失敗會取消其餘工作並等待結束，全部成功後才上傳 Manifest／簽章。取消不會刪除已成功上傳的物件，重試 Upload 時會略過已存在且大小符合的物件；發布前仍須執行 R2 深度驗證。

## 第一次設定

### 1. 修改產品設定

檢查：

```text
config/product.json
config/allowed-files.json
```

特別修改：

```json
"bucket": "你的-r2-bucket",
"publicBaseUrl": "https://patch.example.com"
```

### 2. 產生簽章密鑰

```powershell
dotnet run --project src\PatchPublisher -- keygen `
  --private F:\Secure\GamePatchDepot\signing-private.pem
```

產生：

```text
私鑰：F:\Secure\GamePatchDepot\signing-private.pem
公鑰：config\signing-public.pem
```

私鑰規範：

- 不放在專案目錄。
- 不提交 Git。
- 至少保留一份離線備份。
- 玩家 Launcher 只內建公鑰。

### 3. 一次性保存 R2 憑證

先在目前 PowerShell 工作階段設定以下值：

```powershell
$env:PATCH_SIGNING_PRIVATE_KEY_FILE = "F:\Secure\GamePatchDepot\signing-private.pem"
$env:PATCH_R2_ACCOUNT_ID = "你的 Cloudflare Account ID"
$env:PATCH_R2_ACCESS_KEY_ID = "你的 R2 Access Key ID"
$env:PATCH_R2_SECRET_ACCESS_KEY = "你的 R2 Secret Access Key"
```

也可以用完整 Endpoint 取代 Account ID：

```powershell
$env:PATCH_R2_ENDPOINT = "https://<account-id>.r2.cloudflarestorage.com"
```

接著執行一次：

```powershell
.\setup-credentials.ps1
```

憑證會透過 Windows DPAPI 加密後存放在：

```text
%LOCALAPPDATA%\GamePatchDepot\credentials.json
```

加密檔只能由同一臺 Windows、同一個使用者解開，不會存入專案或 Git。之後一律使用 `.\patch.ps1`，不需要再次設定環境變數。

## 舊版 NX 命令列流程

新版本請優先使用 UI 或 [完整客戶端流程](docs/FULL_CLIENT_PUBLISHING.md)。以下是不加 `--full-client` 的舊版相容用法。

假設：

- 目前正式來源在 `F:\MSPrivate\NoLifeWzToNx\wz\n`。
- 本次以 `UI40.nx` 取代玩家的 `UI.nx`。
- 新版本為 `2026.09.23.1`。
- contentVersion 為 `1008`。

### 1. Prepare

```powershell
.\patch.ps1 prepare `
  --release 2026.09.23.1 `
  --content-version 1008 `
  --base 2026.09.07.1 `
  --source F:\MSPrivate\NoLifeWzToNx\wz\n `
  --replace UI.nx=F:\MSPrivate\NoLifeWzToNx\wz\UI40.nx `
  --description "更新 UI 資源"
```

Prepare 會以 `--base` 的已簽章 Manifest 比對 SHA-256，Candidate 只複製有變更的 NX；Build 再把未變更的舊檔案與本次變更合成完整 Manifest。若是第一版而沒有 `--base`，才會複製全部 NX。來源目錄不會被修改。

### 2. Build

```powershell
.\patch.ps1 build `
  --release 2026.09.23.1
```

### 3. 本機驗證

```powershell
.\patch.ps1 verify `
  --release 2026.09.23.1 `
  --local
```

### 4. 查看差異

第一次發布尚無 stable：

```powershell
.\patch.ps1 diff `
  --release 2026.09.23.1 `
  --against none
```

已有 stable：

```powershell
.\patch.ps1 diff `
  --release 2026.09.23.1 `
  --against stable
```

### 5. 上傳 R2

```powershell
.\patch.ps1 upload `
  --release 2026.09.23.1
```

上傳可以安全重跑。已存在且大小一致的內容會跳過。

### 6. 驗證 R2

快速檢查所有物件存在與大小：

```powershell
.\patch.ps1 verify `
  --release 2026.09.23.1
```

正式發布前，先驗證本機與 R2 Manifest 的簽章並確認兩份清單完全一致，再從 R2 下載所有 chunks，核對分塊及完整檔案的 SHA-256 與大小：

```powershell
.\patch.ps1 verify `
  --release 2026.09.23.1 `
  --deep
```

### 7. 發布 Test

```powershell
.\patch.ps1 promote `
  --release 2026.09.23.1 `
  --channel test
```

### 8. 發布 Stable

確認 Test 測試完成後：

```powershell
.\patch.ps1 promote `
  --release 2026.09.23.1 `
  --channel stable
```

### 9. 查看狀態

```powershell
.\patch.ps1 status
```

## 回滾

假設要將內容回到 `2026.09.07.1`，但新 contentVersion 必須增加為 `1009`：

```powershell
.\patch.ps1 rollback `
  --release 2026.09.23.2 `
  --content-version 1009 `
  --to 2026.09.07.1
```

接著依序執行：

```text
upload
verify --deep
promote --channel test
promote --channel stable
```

回滾 Manifest 重新引用舊 chunks，不會重複儲存相同內容。
管理 UI 可直接按右上角「建立退版」，選擇舊版本後會自動產生新的 Release ID 與 Content Version。

## R2 物件結構

Bucket 名稱為 `novastory`：

| 前綴 | 用途 | 是否可直接刪除 |
| --- | --- | --- |
| `channels/` | Test／Stable 目前版本指標 | 不可 |
| `channel-history/` | 頻道切換歷史 | 不建議；預設保留稽核 |
| `manifests/` | 每個 Release 的完整檔案與 chunk 清單、簽章 | 不可覆寫；預設保留 |
| `objects/sha256/` | 真正的遊戲 chunks，相同內容只存一份 | 只能透過 GC 判斷 |
| `locks/` | 操作中的短期鎖 | 不需手動處理，平時可能不存在 |

R2 Dashboard 中的資料夾只是 Key 前綴。程式目前不使用 `tmp/` 前綴。

## 垃圾回收

先產生報告，不刪除：

```powershell
.\patch.ps1 gc --dry-run
```

人工閱讀 `reports\garbage-collection\<report-id>.json` 後：

```powershell
.\patch.ps1 gc `
  --apply reports\garbage-collection\<report-id>.json
```

Apply 會重新檢查：

- stable／test generation 未改變。
- 目前所有 Manifest 引用。
- 最近三個 Stable。
- 目前 Test。
- 14 天孤兒寬限期。

GC 目前只刪除不再被保留版本引用的 `objects/sha256/`；Manifest 與頻道歷史預設保留，因為容量很小且具有稽核價值。

本機 `work/candidates/<releaseId>` 在 Manifest、簽章與本機驗證都完成後，可以依 SOP 提前清理。`work/chunks` 不會自動清理，未發布版本、重新上傳、退版與本機驗證仍可能需要。

## 取消與回退

- 執行中的操作：UI 按「取消目前操作」，先合作式取消並清理本次暫存；逾時才強制終止。
- Prepare 後、Build 前：按「回退上一步」捨棄 Candidate，可沿用原 Release ID 與 Content Version 重新 Prepare。
- Build 後、尚未進入頻道：Manifest 不可覆寫；按「完全取消版本」保留取消紀錄，改用新版本號。
- Upload 後取消：R2 不可變物件不立即硬刪，未引用 objects 經 14 天寬限期後由 GC 處理。
- 已發布 Test／Stable：不能直接取消或降低 Content Version，必須建立更高版本的退版。

## Cloudflare Cache Rules

需要在 Cloudflare Dashboard 人工設定，順序不可顛倒：

1. `/channels/*`：Bypass Cache。
2. `/channel-history/*`：Eligible for Cache，長 TTL。
3. `/manifests/*`：Eligible for Cache，長 TTL。
4. `/objects/*`：Cache Everything／Eligible for Cache，長 TTL。

正式下載使用自訂網域，不使用 `r2.dev`。

## 管理工具回應測試

```powershell
dotnet run --project tests/PatchManager.SmokeTests -c Release
```

離線模擬 1,500 行進度與錯誤輸出，檢查兩個分頁的紀錄完整且順序正確、最後一批紀錄不遺失，並確認 UI 仍可處理輸入事件及維持操作中的按鈕保護。不開啟視窗、不連線 R2；`build-release.ps1` 也會執行此檢查。

## 尚未完成的外部設定

程式本身可以編譯與完成本機測試，但以下內容必須由 R2 帳號擁有者提供後才能做正式環境驗證：

- R2 Bucket 名稱。
- R2 Account ID。
- R2 Access Key ID 與 Secret Access Key。
- 正式下載網域。
- Cloudflare Cache Rules。

不要將上述秘密貼進 Issue、Git、README 或聊天紀錄。
