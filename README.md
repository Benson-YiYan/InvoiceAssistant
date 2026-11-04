# 发票小秘书 for Windows

Windows 版本采用 WinUI 3 和 Windows App SDK 2.6.5。最低支持 Windows 10 1809，推荐 Windows 11。

## 当前进度

- WinUI 3 原生工作台，支持调整窗口大小。
- 文件夹树、费用类别目录、分层新建与删除。
- 多选发票、金额与类别编辑、合计、CSV 导出、删除归档记录。
- PDF、DOCX、PNG、JPG/JPEG 导入；使用 Windows OCR 识别 PDF 页面和图片。
- 首次启动时读取旧版 Windows Electron 数据目录 `%APPDATA%\invoice-sorter`，原目录不修改；原生版数据保存在 `%LOCALAPPDATA%\InvoiceAssistant`。
- `.doc` 暂需 Microsoft Word 转存为 `.docx` 后导入。

AI API/Codex/DeepSeek Harness 和 GitHub 应用内更新仍待接入，不会在当前构建中伪装为可用功能。

## 本机构建

需要 Windows 10/11、.NET 10 SDK 和 Inno Setup 6：

```powershell
dotnet publish InvoiceAssistant.WinUI/InvoiceAssistant.WinUI.csproj -c Release -r win-x64 --self-contained true -p:WindowsAppSDKSelfContained=true
& "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" installer/InvoiceAssistant.iss
```

## GitHub Actions

`.github/workflows/windows-build.yml` 在 GitHub 托管的 `windows-2022` runner 上构建并生成独立的 x64 `.exe` 安装程序，产物以 Actions artifact 提供。
