# 啟動器發布：簡單操作說明

遊戲更新用「遊戲更新」分頁；NovaLink 本身更新用「啟動器發布」。兩者共用已設定的 R2 連線，但版本、金鑰與儲存路徑分開。

兩個分頁使用相同位置：右上角是重設與操作說明，上方卡片是遠端狀態，左側依序為版本資料、檔案來源與發布流程，右側是進度、取消與執行紀錄。啟動器沒有 Test 或退版功能，不會顯示不適用的操作按鈕。

## 三個步驟

1. **選套件並驗證**：在「檔案來源」按「選擇」，選包含 `launcher` 子目錄的根目錄。例如 `E:/Projects/maplelink/release-packages/launcher-0.6.2`。按「驗證套件」，程式自動顯示版本、大小、SHA-256 與更新說明，不能直接修改；長路徑與完整更新說明可懸停查看。
2. **上傳並回驗**：按「上傳並回驗」。只上傳四個版本檔案，再透過 R2 API 下載核對內容，有位元組進度。這時玩家使用的 Stable 尚未改變。
3. **正式發布**：先完成 EXE 測試啟動，再按「發布 Stable」並確認。後端會再次驗證本機套件、重新下載四個遠端檔案核對，最後才切換 `launcher/channels/stable.json`。

回驗目前使用 R2 S3 API，並非玩家公開下載網址。正式開放玩家前，仍須檢查公開網址、快取設定，以及真實下載／替換／重啟流程。

## 要選哪一層？

```text
launcher-0.6.2/             ← 選這個資料夾
  launcher/
    releases/0.6.2/
      NovaLink.exe
      NovaLink.exe.sig
      manifest.json
      manifest.json.sig
    channels/
      stable.json
```

只讀取及上傳上述五個固定路徑。不要選遊戲客戶端，也不要把私鑰放進套件。

## 防呆與取消

- 只信任程式內建的 NovaLink 公鑰；不接受套件自帶公鑰。支援的 Minisign 規則與 Rust 啟動器一致，包含預雜湊及受信任註解簽章。
- 每次操作都重新驗證；確認後套件被更動，會停止，不能悄悄發布另一份內容。
- 同版號內容不同會拒絕，不覆寫。已有相同檔案會下載核對後略過，不重傳。
- 不允許降低版本。同版號同內容可安全重試；要修正已發布版本，請製作更高版號的新套件。
- 正式發布採 ETag 條件式寫入。另一位管理者先發布時，本次會停止，不覆蓋對方的頻道。
- 取消上傳不會刪除已完成的檔案；重試時會核對略過。取消發布時，伺服器可能已完成最後切換，UI 會重新查詢遠端狀態；查詢失敗就顯示無法確認，不會假裝退版成功。
- 右上角「重設／下一版」只清除套件選擇與驗證狀態，不刪檔或退版。重開程式保留套件路徑，但須重新驗證與回驗，不沿用上次的成功狀態。
- 啟動器操作不修改遊戲 Candidate、分塊、版本或 GC；遊戲 GC 不會掃描 `launcher/`。

## 套件製作與金鑰

這個分頁負責驗證與發布「已簽章套件」，不負責編譯 NovaLink 或產生私鑰。先在 maplelink 專案使用 `scripts/prepare-launcher-r2.ps1` 製作套件。

GamePatchDepot 不需要 NovaLink 私鑰。私鑰仍保存在簽署電腦的專案外；公鑰識別碼為 `59ED581CC0612FEF`。如將來更換公鑰，必須協調啟動器與發布端一起更新，不能從未驗證的套件匯入信任。

新公鑰的首版必須手動部署。Minisign 不等於 Windows Authenticode，不保證消除 SmartScreen 提示。啟動器目前只使用 Stable 與完整 EXE 下載，沒有啟動器 Test 頻道或差異補丁。

## 命令列（選用）

在 GamePatchDepot 根目錄使用原有 `patch.ps1`，會沿用 Windows 儲存的 R2 憑證，不必每次設定環境變數：

```powershell
.\patch.ps1 launcher-verify --bundle E:\Projects\maplelink\release-packages\launcher-0.6.2
.\patch.ps1 launcher-upload --bundle E:\Projects\maplelink\release-packages\launcher-0.6.2
.\patch.ps1 launcher-status
```

正式發布使用 `launcher-publish --bundle <套件目錄> --expected-bundle <驗證輸出的 BundleSha256> --confirm`。圖形介面會自動帶入套件雜湊，不需要手打。

驗證演算法使用 [Bouncy Castle 官方 NuGet 套件](https://www.nuget.org/packages/BouncyCastle.Cryptography/2.7.0)，測試包含由 Tauri CLI 產生、同時供 Rust 驗證的拋棄式金鑰資料；不使用正式私鑰。

## GitHub Actions

GamePatchDepot 放入 Private GitHub repository 後使用三條 workflow：

- `ci.yml`：Push／Pull Request 時執行 restore、build 與 tests。
- `launcher-upload.yml`：手動輸入 NovaLink tag，從 Private Release 下載已簽章套件，上傳四個不可變檔案並回驗；不切換 Stable。
- `launcher-promote.yml`：手動輸入相同 tag、Upload 顯示的 Bundle SHA-256 與確認版號；拒絕 Prerelease，重新驗證後切換 Stable，再從玩家公開網址回驗。

Repository Secrets：

```text
NOVALINK_RELEASE_TOKEN
PATCH_R2_ACCOUNT_ID
PATCH_R2_ACCESS_KEY_ID
PATCH_R2_SECRET_ACCESS_KEY
```

`NOVALINK_RELEASE_TOKEN` 只需讀取 `j84637587/novalink` Private Release。R2 Token 使用指定 bucket 的 Object Read & Write 權限。NovaLink 更新私鑰不放在 GamePatchDepot。

每次發布紀錄 NovaLink tag、Bundle SHA-256、GamePatchDepot commit SHA 與 workflow run ID。Upload 與 Promote 使用相同 concurrency group，不會同時執行。
