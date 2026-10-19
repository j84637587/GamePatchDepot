# NovaLink 上傳與發布 R2

完整流程請照 [NovaLink 發布 SOP](https://github.com/j84637587/novalink/blob/main/docs/deployment.md)：**確認版本 → Build → Upload → 測試後 Promote**。

先完成 NovaLink 的 Build and Release，取得已簽章的 GitHub Release，才能操作本頁。以下用 `v1.1.0` 示範；這是 NovaLink 版本，不是遊戲內容版本。

## GitHub Actions（建議）

1. 開啟 [Upload NovaLink launcher candidate](https://github.com/j84637587/GamePatchDepot/actions/workflows/launcher-upload.yml)，按 **Run workflow**，分支選 `main`，`novalink_tag` 填 `v1.1.0`。
2. 等整個 run 成功，從 **Summary** 複製 **Bundle SHA-256**。這時玩家使用的 Stable 尚未改變；先依[隔離更新驗收](https://github.com/j84637587/novalink/blob/main/docs/release-reference.md#候選版本的隔離更新驗收)，用正式舊版來源製作的測試 build 完成候選下載、驗章、替換與重啟，確認新版啟動與登入。只有直接開新版 EXE 不算完成。
3. 確認可以發布後，開啟 [Promote NovaLink launcher to stable](https://github.com/j84637587/GamePatchDepot/actions/workflows/launcher-promote.yml)，按 **Run workflow**，分支選 `main`：
   - `novalink_tag`：`v1.1.0`。
   - `bundle_sha256`：貼上剛才同一次 Upload Summary 的 **Bundle SHA-256**，不是 EXE 或 ZIP 的雜湊。
   - `confirm_version`：`1.1.0`，不加 `v`。
4. Promote 成功後，確認 Summary 版號正確，再用舊版 NovaLink 複查正式網址的更新、替換與重啟；不能用這一步取代前面的隔離驗收。

**只有 Promote 會切換玩家的 Stable。** NovaLink 的 GitHub Release 要選 `release`；`prerelease` 不能發布成 Stable。

## 桌面工具（選用）

桌面工具是另一種操作方式，與上面的 Actions 擇一即可。

從 NovaLink Release 下載 `NovaLink-launcher-1.1.0.zip` 並解壓，開啟 GamePatchDepot 的「啟動器發布」分頁：

1. 選擇解壓後**包含 `launcher` 子目錄的根目錄**，按「驗證套件」。不要只選 EXE 或 ZIP。
2. 按「上傳並回驗」，等待成功；這時 Stable 尚未改變。
3. 完成同一候選套件的隔離舊版更新、替換、重啟與啟動／登入驗收後，按「發布 Stable」並確認版號。驗收環境尚未備妥時，先停在上傳。

例如選取下列 `NovaLink-launcher-1.1.0` 資料夾：

```text
NovaLink-launcher-1.1.0/
  launcher/
    releases/1.1.0/
    channels/stable.json
```

桌面工具用 R2 API 回驗；正式發布後，還要確認玩家公開網址能取得新版，並實測更新與重啟。Actions 的 Promote 已包含公開網址回驗。

## 失敗或取消

- 網路、憑證或中斷造成的 Upload 失敗：修復連線或憑證後，以相同套件與 tag 重試；相同內容會核對後略過，不必重新 Build。
- 驗章、雜湊或內容錯誤：先停止查明原因；若 Release 套件有問題，修正來源並發布更高版號，不反覆重試同 tag，不覆寫原檔。
- Promote／「發布 Stable」失敗或取消：可能已完成切換。先重新查詢工具的遠端狀態，或在 GamePatchDepot 目錄執行 `.\patch.ps1 launcher-status`。
- 狀態無法確認時先停止發布，不假設已退回舊版。
- 同版不同內容、降版或同時發布造成的衝突會被拒絕。已發布版本有問題時，依[復原說明](https://github.com/j84637587/novalink/blob/main/docs/release-reference.md#發布後有問題)製作更高版號 hotfix。

<details>
<summary>維護者：首次設定與命令列</summary>

GamePatchDepot repo 的 Actions Secrets：

- `NOVALINK_RELEASE_TOKEN`：僅需讀取 NovaLink Private Release。
- `R2_ACCOUNT_ID`、`R2_ACCESS_KEY_ID`、`R2_SECRET_ACCESS_KEY`：指定 bucket 的 Object Read & Write。

更新私鑰留在 NovaLink 的簽署端，不放入本 repo 或發布套件。完整設定見 [NovaLink 維護者參考](https://github.com/j84637587/novalink/blob/main/docs/release-reference.md)。

在 GamePatchDepot 目錄執行命令列時，會沿用 Windows 已儲存的 R2 憑證：

```powershell
.\patch.ps1 launcher-verify --bundle "<解壓後的套件根目錄>"
.\patch.ps1 launcher-upload --bundle "<解壓後的套件根目錄>"
.\patch.ps1 launcher-status
.\patch.ps1 launcher-publish --bundle "<解壓後的套件根目錄>" --expected-bundle <驗證輸出的BundleSha256> --confirm
```

- 工具只信任內建 NovaLink 公鑰，每次重新驗證套件與遠端檔案；不接受套件自帶公鑰建立信任。
- 已存在的相同內容可安全重試；不同內容不覆寫。發布以 ETag 條件式寫入避免覆蓋其他管理者的結果。
- 取消 Upload 不刪除已完成檔案；「重設／下一版」只清除操作狀態，不會刪檔或退版。
- 啟動器使用獨立 `launcher/` 路徑；不修改遊戲版本、Candidate、分塊或 GC，也沒有啟動器 Test 頻道。
- 發布記錄包含 NovaLink tag、Bundle SHA-256、GamePatchDepot commit 與 run ID。

</details>
