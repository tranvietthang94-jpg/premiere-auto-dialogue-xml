#ifndef SourceDirectory
  #error SourceDirectory is required
#endif
#ifndef OutputDirectory
  #error OutputDirectory is required
#endif
#ifndef OutputBaseFilename
  #error OutputBaseFilename is required
#endif
#ifndef AppVersion
  #error AppVersion is required
#endif

#define AppName "Premiere Auto Dialogue XML"
#define AppExecutable "PremiereAutoDialogueXml.exe"
#define AppPublisher "tranvietthang94-jpg"
#define AppUrl "https://github.com/tranvietthang94-jpg/premiere-auto-dialogue-xml"

[Setup]
AppId={{99295721-E0F1-4299-85FF-51B020C49600}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
SetupArchitecture=x64
MinVersion=10.0
OutputDir={#OutputDirectory}
OutputBaseFilename={#OutputBaseFilename}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
WizardResizable=yes
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousGroup=yes
CloseApplications=yes
RestartApplications=no
Uninstallable=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExecutable}
InfoBeforeFile=INFO-BEFORE.vi.txt
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription=Bộ cài {#AppName}
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
#ifdef SignToolName
SignTool={#SignToolName}
SignedUninstaller=yes
#endif

[Tasks]
Name: "desktopicon"; Description: "Tạo biểu tượng trên Desktop"; GroupDescription: "Lối tắt bổ sung:"; Flags: unchecked

[Files]
Source: "{#SourceDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExecutable}"; WorkingDir: "{app}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExecutable}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExecutable}"; Description: "Mở {#AppName}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Messages]
SetupAppTitle=Cài đặt
SetupWindowTitle=Cài đặt - %1
UninstallAppTitle=Gỡ cài đặt
UninstallAppFullTitle=Gỡ cài đặt %1
InformationTitle=Thông tin
ConfirmTitle=Xác nhận
ErrorTitle=Lỗi
ButtonBack=< &Quay lại
ButtonNext=&Tiếp tục >
ButtonInstall=&Cài đặt
ButtonCancel=Hủy
ButtonFinish=&Hoàn tất
ButtonBrowse=&Chọn...
ButtonWizardBrowse=&Chọn...
ButtonOK=Đồng ý
ButtonYes=&Có
ButtonNo=&Không
ButtonNewFolder=&Tạo thư mục mới
ClickNext=Chọn Tiếp tục để đi tiếp hoặc Hủy để thoát trình cài đặt.
WelcomeLabel1=Chào mừng bạn đến với trình cài đặt [name]
WelcomeLabel2=Trình cài đặt sẽ cài [name/ver] trên máy tính.%n%nBạn nên đóng các ứng dụng khác trước khi tiếp tục.
WizardInfoBefore=Thông tin quan trọng
InfoBeforeLabel=Vui lòng đọc thông tin sau trước khi tiếp tục.
InfoBeforeClickLabel=Khi đã sẵn sàng, chọn Tiếp tục.
WizardSelectDir=Chọn thư mục cài đặt
SelectDirDesc=[name] sẽ được cài ở đâu?
SelectDirLabel3=Trình cài đặt sẽ cài [name] vào thư mục sau.
SelectDirBrowseLabel=Chọn Tiếp tục hoặc chọn một thư mục khác.
DiskSpaceGBLabel=Cần ít nhất [gb] GB dung lượng đĩa trống.
DiskSpaceMBLabel=Cần ít nhất [mb] MB dung lượng đĩa trống.
DiskSpaceWarningTitle=Không đủ dung lượng đĩa
DiskSpaceWarning=Trình cài đặt cần ít nhất %1 KB dung lượng trống, nhưng ổ đĩa đã chọn chỉ còn %2 KB.%n%nBạn vẫn muốn tiếp tục?
WizardSelectTasks=Chọn tác vụ bổ sung
SelectTasksDesc=Bạn muốn tạo thêm lối tắt nào?
SelectTasksLabel2=Chọn các tác vụ bổ sung rồi chọn Tiếp tục.
WizardReady=Sẵn sàng cài đặt
ReadyLabel1=Trình cài đặt đã sẵn sàng cài [name].
ReadyLabel2a=Chọn Cài đặt để tiếp tục hoặc Quay lại để kiểm tra các lựa chọn.
ReadyLabel2b=Chọn Cài đặt để tiếp tục.
ReadyMemoDir=Thư mục cài đặt:
ReadyMemoGroup=Thư mục Start Menu:
ReadyMemoTasks=Tác vụ bổ sung:
WizardInstalling=Đang cài đặt
InstallingLabel=Vui lòng chờ trong khi [name] được cài đặt.
FinishedHeadingLabel=Hoàn tất cài đặt [name]
FinishedLabelNoIcons=[name] đã được cài đặt thành công.
FinishedLabel=[name] đã được cài đặt thành công. Bạn có thể mở ứng dụng từ lối tắt đã tạo.
ClickFinish=Chọn Hoàn tất để đóng trình cài đặt.
ExitSetupTitle=Thoát trình cài đặt
ExitSetupMessage=Quá trình cài đặt chưa hoàn tất. Nếu thoát bây giờ, ứng dụng sẽ chưa được cài.%n%nBạn muốn thoát trình cài đặt?
StatusCreateDirs=Đang tạo thư mục...
StatusExtractFiles=Đang chép tệp...
StatusCreateIcons=Đang tạo lối tắt...
StatusSavingUninstall=Đang lưu thông tin gỡ cài đặt...
StatusRunProgram=Đang hoàn tất...
UninstallStatusLabel=Vui lòng chờ trong khi %1 được gỡ khỏi máy tính.
UninstalledAll=%1 đã được gỡ khỏi máy tính.
