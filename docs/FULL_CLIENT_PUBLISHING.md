# 完整客戶端發布操作

2026-09-26：已實作管理端收錄、Build、完整清單驗證與 UI；maplelink 自動下載／套用尚未串接。本次未上傳任何實際客戶端或切換遠端頻道。

## 你現在怎麼操作

1. 準備可以正常執行的客戶端根目錄，包含 EXE、DLL、NX 與必要子目錄。不要選只有 NX 的資料夾。
2. 檢查 `config/client-files.json` 的排除、保留與必備檔案設定。
3. 打開 PatchManager，使用新的 Release ID，選擇「完整客戶端目錄」。若畫面回到舊 Candidate，先完成或取消舊版本，再建立新版本；既有 NX Candidate 不會自動變成完整客戶端。
4. 按 Prepare，先在檔案清單勾選收錄項目。不想發布的取消勾選，再按「確認並建立 Candidate」。確認前不會複製檔案；取消視窗不會建立 Candidate。來源目錄本身不會被改動。
5. 確認後 Build，再執行本機驗證與 Diff。一般更新的本機驗證只讀新增或變更的檔案；第一版或沒有基準資料時讀取全部。若有檔案要移除，UI 會在 Build 前要求確認。
6. 確認內容後才 Upload、R2 深度驗證、發布 Test。完整客戶端玩家端串接及遊戲測試完成後再發布 Stable。

目前只檢查「至少有一個 EXE」與你設定的必備檔案，不能據此證明客戶端能正常遊玩。請把真正的遊戲 EXE 與必要 DLL 加入 `requiredFiles`，並保留遊戲測試流程。

## 收錄規則

來源的一般檔案會遞迴列出，不限制副檔名。UI 預設勾選未被規則排除、也未被基準版本標記保留的檔案，由使用者確認。空檔案可發布；空資料夾不另記錄。拒絕 junction／symlink、路徑跳脫、Windows 不合法路徑與大小寫／檔案目錄衝突。

### 勾選範例

| 檔案 | 怎麼選 | 結果 |
|---|---|---|
| `Game.exe`、`data/UI.nx` | 保持「收錄」勾選 | 納入完整版本；相同內容不再複製 |
| `settings.ini` | 取消「收錄」 | 不發布；舊版若已追蹤，改為保留玩家現有檔案 |
| `obsolete.dll`（舊版有） | 取消「收錄」，再勾「從新版移除」 | 列入新版刪除清單，不刪除本機來源 |
| `secret.pem` | 規則排除，不能勾選 | 不收錄；如確有需要，先修改規則再重新掃描 |

「收錄」是完整版本應有的檔案，不是只有本次修改的檔案。沒改動的遊戲檔案也要保持勾選。

可搜尋相對路徑，對搜尋結果批次勾選／取消，或重設全部。畫面的容量是所選檔案總量，不是新增上傳量。檔名與說明太長時可懸停查看。至少須保留一個 EXE，以及 `requiredFiles` 指定的檔案。

預覽只讀取清單、大小與修改時間，不先雜湊大型檔案。確認時會重新檢查來源、替換檔、規則與基準；若清單、大小或修改時間有變化，會要求重新掃描。這不是對檔案內容的鎖定，Prepare 期間仍不要修改來源。

未確認的勾選不會自動保存。確認後會記錄在 Candidate 的收錄、排除、保留與刪除清單；後續以此版本為發布基準時，保留項目預設不勾選，可重新納入。固定長期排除的項目，建議寫進 `client-files.json`。

`config/client-files.json`：

| 欄位 | 用法 |
|---|---|
| excludedDirectories | 任一層符合名稱的資料夾，其內容不發布，例如 `logs`、`screenshots` |
| excludedFiles | 符合檔名的不發布，例如 `credentials.json` |
| excludedExtensions | 符合副檔名的不發布，例如 `.log`、`.pem` |
| preservedPaths | 安裝根目錄下要保留的相對檔案或資料夾，例如 `settings.ini`、`userdata` |
| requiredFiles | 必須收錄的相對檔案，例如 `Game.exe`、`data/client.dll` |

路徑統一用 `/`，資料夾不加結尾 `/`，目前不接受萬用字元。排除目錄內若有連結仍會拒絕，以免掃描離開來源。內建排除規則不是秘密資料偵測器，發布來源應為乾淨的發行目錄。

INI／JSON 有些是必要遊戲資料、有些是玩家設定，不能全部依副檔名排除；請把實際玩家設定加入 `preservedPaths`。預設規則仍以目前設定檔為準，可依客戶端需要調整。

## Candidate 與版本

2026-09-27：已增加「額外二進位差異補丁」。Build 會針對相較基準有變更的檔案產生並實際套用驗證補丁，完整分塊仍保留作為備援。第一版無基準時不產生補丁；玩家端仍待接入。操作與限制見 [差異補丁說明](DELTA_PATCHES.md)。

```text
work/candidates/<release>/
  release-info.json       發布中繼資料、完整收錄清單、變更與排除紀錄
  files/                 只放本次變更檔案，保留原本相對路徑
    Game.exe
    data/client.dll
```

Build 輸出的 schemaVersion 2 Manifest 記錄完整版本。未變更的檔案沿用已驗證的基準 Manifest。UI 清單中，舊版有但來源已不存在的檔案，預設「停止追蹤、保留玩家檔案」；只有明確勾「從新版移除」才加入 `delete`。只移除或只調整保留清單、沒有新增內容，也可以建立版本。

`preserve` 記錄已排除或指定保留的路徑；若既有受管設定改成保留，會停止追蹤但不加入 `delete`。未重新納入管理的保留紀錄會沿用至後續版本。玩家端實作時必須遵守它，尤其從很舊的版本直接更新時；目前尚未實作玩家端刪除或套用。

第一次由 NX 基準轉成完整客戶端時，相同路徑且相同內容的 NX 可重用既有 chunks。新 schema 沿用目前 R2 路徑、簽章與單調遞增版本；沒有新增 bucket 或自動切換頻道。分塊仍使用 `product.json` 中的既有設定，目前為 256 MiB。

目前 UI 已有完整客戶端版本可退版時，只列出完整客戶端目標，避免誤選只有 NX 的版本。CLI 維護舊 NX 時仍可明確指定舊版；操作人員不可把 NX-only 清單當作完整客戶端退版。

## CLI 範例

下面的來源路徑及版本號是示意，請換成實際值；第一次發布不加 `--base`。

```powershell
.\patch.ps1 prepare --full-client --release 2026.09.26.1 --content-version 4 --source F:\ClientRelease --description "feat(client): 完整客戶端"
.\patch.ps1 build --release 2026.09.26.1
.\patch.ps1 verify --release 2026.09.26.1 --local
.\patch.ps1 diff --release 2026.09.26.1 --against stable
```

後續 Prepare 加 `--base <已發布版本>`，或使用 UI 自動選擇發布基準。CLI 不加 `--full-client` 會維持舊 NX 模式。

更新版可用 `verify --release <版本> --local --changed`，依 Candidate 記錄的基準與兩份已簽章 Manifest，只驗證新增、內容或分塊配置變更的檔案；補丁也會驗證。無 Candidate、無基準及第一版自動完整驗證。`verify --local` 始終完整驗證所有檔案，適合定期檢查本機舊分塊。變更驗證通過不代表未變更的本機分塊沒有損壞；後續 R2 深度驗證仍核對雲端完整版本。

CLI 直接 `prepare --full-client` 不帶 `--selection`，仍沿用依規則自動收錄與推導刪除的行為，沒有 UI 的逐項確認。一般操作建議使用 UI。

需要 CLI 預覽時，先執行 `preview --source <目錄> --output <新清單.json> [--base <版本>]`，在 JSON 中只調整各項的 `include`／`remove`，再於 `prepare --full-client` 加上 `--selection <清單.json>`。兩次的來源、基準與 `--replace` 必須一致，既有輸出檔不覆寫。UI 會自動處理這些步驟與暫存清理。

## 已驗證與尚待驗證

### 上傳內容是否與本機發布一致？

Upload 後按「R2 深度驗證」。工具會先驗證本機與遠端 Manifest 的簽章，再逐位元組確認兩份清單完全一致；接著下載遠端 chunks，核對每塊及完整檔案的 SHA-256 與大小。驗證基準是本機 Build 已固定的版本，不是之後可能被修改的來源資料夾。

全部通過後才寫入驗證報告，雜湊取自這次實際驗過的遠端 Manifest。報告格式為 `schemaVersion: 2`；舊格式必須重新深度驗證。重新深度驗證開始時會撤銷前次通過紀錄，失敗或取消不得沿用；通過前也會再檢查本機清單未被替換。

一般 R2 驗證及發布前檢查也會確認兩份 Manifest 一致，但只有「深度驗證」會下載內容核對雜湊。發布頻道使用的 Manifest 雜湊直接取自已驗過的清單。這些檢查走 R2 API，尚不等同玩家公開下載網址或完整安裝流程的端到端驗收。

自動測試包含：子目錄 EXE／DLL／NX、空檔案、與中繼資料同名的客戶端檔案、逐檔重組比對、單檔變更、只移除檔案、差異報告、退版、排除與保留規則、必備檔缺漏、非法路徑、取消 Candidate 後重建及舊 NX 升級。檔案勾選另測試 JSON 往返、取消收錄、明確移除、來源消失時保留、來源及規則變更攔截，以及必備／排除規則不可繞過。

實際完整客戶端目錄尚未收到，因此真實檔案盤點、遊戲啟動、完整客戶端 R2 上傳／深度驗證與 maplelink 端到端測試仍待完成。

## NovaStory 冷啟動票證

新交接方式會等遊戲載入完成後才申請登入票證，避免慢速硬碟載入期間就耗掉 60 秒有效期。

- 先使用支援 `--novalink-ticket-ready-stdin` 的新版 NovaStory，並讓玩家更新支援此流程的 NovaLink。
- Prepare 完成、Build 之前，在該 Candidate 的 `release-info.json` 加上 `"launcherTicketProtocol": 2`，再照原流程 Build、驗證及發布 Test。
- 測試確認後再發布 Stable。舊遊戲請維持 `1` 或不填；請勿直接修改已簽章的 Manifest。回滾會沿用目標版本的協定。

協定欄位會納入 Manifest 簽章；它是發布者對遊戲能力的聲明，工具無法代替實際遊戲驗證。省略欄位時預設使用舊交接方式，以相容已發布版本。
