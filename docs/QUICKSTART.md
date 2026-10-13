# 發布快速指南

**2026-09-26：UI 新建版本已支援完整客戶端。** 請選擇包含 EXE、DLL、NX 的客戶端根目錄，依 [完整客戶端操作說明](FULL_CLIENT_PUBLISHING.md) 設定排除與保留規則。下方原本的 NX 命令列範例保留供舊版本維護；新版本 CLI 需加 `--full-client`。

首次設定 R2 環境變數後執行一次：

```powershell
cd F:\MSPrivate\GamePatchDepot
.\setup-credentials.ps1
```

之後的命令會自動讀取 Windows DPAPI 加密憑證，不需要重複輸入環境變數。

日常操作可直接開啟：

```powershell
.\dist\win-x64\PatchManager.exe
```

CLI 保留作為自動化、救援與除錯用途。

UI 右上角的「操作說明」包含完整流程；每個欄位旁的 `?` 可用滑鼠停留查看填寫範例。「建立退版」可選擇舊版本並自動產生新版本號。按鈕會依 Candidate、Manifest、深度驗證與 Test 狀態自動啟用，避免跳步發布。

**Prepare 會先讓你確認檔案。** 選好完整客戶端資料夾後按 Prepare，在清單裡取消不想發布的項目，再按「確認並建立 Candidate」。未改動的遊戲檔案保持勾選即可，不會重複複製。取消勾選不會刪除玩家檔案；要刪除舊檔，另外勾「從新版移除」。取消確認視窗不會建立版本。

關閉 UI 不會取消版本。下次開啟時，系統會自動找到最新的未完成版本，並還原 Release ID、Content Version、來源資料夾、UI 替換檔與更新說明。已完成的 R2 深度驗證也會以 Manifest 雜湊確認後還原；驗證報告必須為新版 `schemaVersion: 2`，舊紀錄需重新驗證。新版會確認本機與遠端 Manifest 完全一致，再下載核對分塊及完整檔案的 SHA-256。

## 每次正式發布

```text
Prepare
  ↓
Build
  ↓
Verify --local
  ↓
Diff
  ↓
Upload
  ↓
Verify --deep
  ↓
Promote test
  ↓
玩家更新與遊戲測試
  ↓
Promote stable
  ↓
監看 30～60 分鐘
```

範例：

```powershell
cd F:\MSPrivate\GamePatchDepot

.\patch.ps1 prepare --release 2026.09.23.1 --content-version 1008 --source F:\MSPrivate\NoLifeWzToNx\wz\n --replace UI.nx=F:\MSPrivate\NoLifeWzToNx\wz\UI40.nx --description "更新 UI 資源"
.\patch.ps1 build --release 2026.09.23.1
.\patch.ps1 verify --release 2026.09.23.1 --local
.\patch.ps1 diff --release 2026.09.23.1 --against stable
.\patch.ps1 upload --release 2026.09.23.1
.\patch.ps1 verify --release 2026.09.23.1 --deep
.\patch.ps1 promote --release 2026.09.23.1 --channel test
.\patch.ps1 promote --release 2026.09.23.1 --channel stable
.\patch.ps1 status
```

## 發布前必查

```text
[ ] Release ID 沒有使用過
[ ] contentVersion 比目前 Stable 大
[ ] Candidate 只含相較發布基準有變更的 NX，且沒有未允許檔案
[ ] Diff 只有預期的變更
[ ] 本機 Verify 通過
[ ] R2 Deep Verify 通過
[ ] Test 頻道更新成功
[ ] 遊戲啟動、登入、選角與進入地圖正常
[ ] 最後才切換 Stable
```

## 發生問題時

```text
不要刪除新版本
不要覆寫 Manifest
不要降低 contentVersion

建立一個更高 contentVersion 的回滾版本
→ Promote test
→ 驗證
→ Promote stable
```

## 每月清理

```powershell
.\patch.ps1 gc --dry-run
```

閱讀報告後才執行：

```powershell
.\patch.ps1 gc --apply <report-path>
```

絕對不要手動依檔案日期批次刪除 `objects/sha256/`。

## 本機空間清理

- `work/candidates/<releaseId>`：確認 Manifest、簽章存在且 `verify --local` 通過後，可以依完整 SOP 清理指定版本。
- `work/chunks`：不要跟著 Candidate 一起刪除；未發布版本、重新上傳、退版與本機驗證仍可能使用。
- `catalog`、`config`、`reports`、`dist`：不要當成暫存資料刪除。

R2 的 `channels/`、`channel-history/`、`manifests/` 不需要人工清理。真正占空間的 `objects/sha256/` 只能透過 GC Dry Run／Apply 處理。

## 取消與回退

- 正在執行：按「取消目前操作」。
- 只有 Candidate：按「回退上一步」回到 Prepare 前。
- 已 Build：按「完全取消版本」，同一 Release ID 不再重用。
- 已 Upload：取消後由 14 天 GC 清理未引用 objects。
- 已在 Test／Stable：使用「建立退版」，不能直接把頻道倒回舊 Content Version。
