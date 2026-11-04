#pragma codepage 65001
#define AppName "发票小秘书"
#define AppVersion "0.1.0"
#define AppPublisher "Benson"
#define AppExeName "InvoiceAssistant.WinUI.exe"

[Setup]
AppId={{9C6A772A-6C75-49E4-9D4F-B3F97AB7D875}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\InvoiceAssistant
DefaultGroupName={#AppName}
OutputDir=..\artifacts
OutputBaseFilename=发票小秘书-Setup-{#AppVersion}-x64
ArchitecturesInstallIn64BitMode=x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
UninstallDisplayIcon={app}\{#AppExeName}

[Files]
Source: "..\InvoiceAssistant.WinUI\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: unchecked
