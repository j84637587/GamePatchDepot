# 發布快速指南

新版本統一發布完整客戶端。來源必須包含支援新登入流程的新版 `NovaStory.exe`、DLL、NX 與必要子目錄；不要選只有 NX 的目錄。

## 第一次使用

- 依環境設定 R2 憑證後，執行一次 `./setup-credentials.ps1`；之後使用 Windows DPAPI 保存的憑證。
- 開啟 `./dist/win-x64/PatchManager.exe`。
- 先發布支援新交接的 NovaLink。首次遊戲發布／遷移時，「最低 NovaLink 版本」填該實際已發布版本，必須高於不支援的 1.1.0。

## 每次正式發布

1. 使用新的 Release ID，選擇完整客戶端目錄並填更新說明。
2. 若舊版只有 NX，勾「建立新完整基準」，來源選本次已驗證的新編譯產物；一般更新不用勾。
3. 按 Prepare，確認收錄清單。沒變更的遊戲檔案保持收錄，不會重複複製；不想發布的取消收錄，要移除舊檔時另勾「從新版移除」。
4. Build，完成本機驗證與 Diff；第一版沒有舊版本時略過 Diff。
5. Upload，通過 R2 深度驗證後發布 Test。
6. 實機確認更新、啟動、登入、選角及進入地圖正常。
7. 最後才發布 Stable，並監看上線情況。

舊 NX 模式與舊票證協定不再發布，也不能回滾啟用。UI 會排除不支援的回滾目標。後續最低啟動器版本可留空沿用相容基準，不能低於基準要求。

CLI 自動化與完整收錄規則請使用 [完整客戶端發布操作](FULL_CLIENT_PUBLISHING.md#cli-範例) 的範例；每次 Prepare 都要加 `--full-client`，首次發布／遷移需提供 `--minimum-launcher-version`。

## 發布前確認

- Release ID 未使用過，Content Version 高於目前頻道。
- 差異只包含預期內容，本機與 R2 深度驗證都通過。
- Test 的實際遊戲流程正常，再發布 Stable。

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
