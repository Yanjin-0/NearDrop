# 第三方元件、授權及來源

## Syncthing

使用 **v2.1.7 windows-amd64** 官方穩定發行，未修改 EXE 或其通訊協定。Syncthing 是獨立專案，本軟體不代表其官方產品或取得商標授權。

- 官方專案：https://github.com/syncthing/syncthing
- 固定版來源：https://github.com/syncthing/syncthing/tree/v2.1.7
- 完整對應原始碼可免費取得：https://github.com/syncthing/syncthing/releases/download/v2.1.7/syncthing-source-v2.1.7.tar.gz
- 來源 archive SHA-256：`5b84abd0e8b187f618fb2c38c37fa37ce0b377a275d3e0b981bad94176a24afa`
- Windows archive：https://github.com/syncthing/syncthing/releases/download/v2.1.7/syncthing-windows-amd64-v2.1.7.zip
- Archive SHA-256：`3ce7da770f546a8d02b65b2e7ee31f2a3b8b7c3f990ede045590cca4172ecfe1`
- EXE SHA-256：`cd9aea20afda2b9bdba5aff2e6ac964ed5c2e741ed4ceb48ad9e1be096ca7891`
- 校驗來源：2026-11-18 以 HTTPS 取得官方 GitHub release 的 SHA-256。另以 Windows Authenticode 檢查引擎 EXE，結果為 `Valid`，發行者為 `Syncthing AB`，憑證指紋為 `C00ABBCEFD1825F8A811C72E01CED600C03885D2`。
- 授權：Mozilla Public License 2.0。完整 `LICENSE.txt`、`AUTHORS.txt`、官方 `README.txt` 及 metadata 隨 `engine` 目錄原樣附帶。對 Syncthing 原始碼的權利不因本軟體授權而改變。沒有提供額外保固。

MPL 2.0 授權文字以隨包的 `engine/LICENSE.txt` 為準。若未來修改 MPL 檔案，必須依其條款提供對應修改原始碼及保留通知；目前採獨立程序與官方 REST API，沒有修改上游檔案。

REST 參考：https://docs.syncthing.net/dev/rest.html
設定 API：https://docs.syncthing.net/rest/config.html
完成狀態：https://docs.syncthing.net/rest/db-completion-get.html
命令列：https://docs.syncthing.net/users/syncthing.html

## Microsoft .NET / WPF

安裝包使用自含式 .NET **10.0.15 LTS win-x64**（NETCore 與 WindowsDesktop），由 Microsoft 官方 NuGet runtime 套件取得，還原時使用 NuGet 套件簽章驗證。開發 SDK 10.0.301；普通開發／測試建置可使用開發機既有 .NET 10 runtime，交付的安裝包包含指定修補版。

- 平台授權與第三方通知另附於 `docs/DOTNET-LICENSE.txt`、`docs/DOTNET-THIRD-PARTY.txt` 及 WindowsDesktop 對應通知。
- https://github.com/dotnet/runtime/tree/v10.0.15
- https://github.com/dotnet/wpf/tree/v10.0.15
- 支援政策：https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core

沒有其他應用程式 NuGet 相依套件、付費服務或自架伺服器。NearDrop 自有程式碼授權見專案根目錄 LICENSE。
