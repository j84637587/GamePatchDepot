# 差異補丁工具

固定使用上游 [HDiffPatch v5.1.3](https://github.com/sisong/HDiffPatch/releases/tag/v5.1.3) 的 Windows x64 版本，不從 PATH 尋找任意執行檔。

從專案根目錄執行 `setup-patch-tools.ps1`。安裝程式核對上游發行壓縮檔 SHA-256，再取出 `hdiffz.exe` 與 `hpatchz.exe`。每次執行工具前也會核對固定執行檔雜湊。

- 套件 SHA-256：`77f141386e5d8f785c1c846e10fbbc19b6c05aa00e3f59cc44670fb3f0e2ae94`
- 產生參數：`-s-64k -SD -c-zstd-3 -p-2`
- 套用參數：`-s-8m`
- 格式：HDIFFSF20，zstd 壓縮，發布協定名稱 `hdiffpatch-sf20-zstd`。
- 執行檔不提交到 Git；安裝指令可重跑，但不會覆寫雜湊不符的檔案。

上游 [參數說明](https://github.com/sisong/HDiffPatch/tree/v5.1.3#diff-command-line-usage) 說明串流比對仍需索引記憶體，並不是固定零成本；64 KiB 比對粒度用來降低大型檔案的索引需求。實際大型 NX 的時間與容量仍需量測。

`LICENSE` 保留此版本上游授權文件。NovaLink 已內嵌同版 hpatchz 與第三方授權 notices；工具與完整授權文字位於 `E:/Projects/maplelink/src-tauri/resources/hdiffpatch/`，套用時一併輸出授權文字。
