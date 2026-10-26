; Το αρχείο εγκατάστασης του Voiceboard για Windows (Inno Setup 6).
; Το χτίζει το GitHub Actions: iscc /DAppVersion=1.0.0 /DSourceDir=..\publish installer\Voiceboard.iss
; Εγκατάσταση μόνο για τον χρήστη (%LOCALAPPDATA%\Programs\Voiceboard), χωρίς δικαιώματα διαχειριστή.
; Αθόρυβη εγκατάσταση (για το AI του θεατή): VoiceboardSetup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6F1C2B7E-5A4D-4C1B-9E3F-2B8D7A6C5E41}
AppName=Voiceboard
AppVersion={#AppVersion}
AppPublisher=Philip Tzimopou
AppPublisherURL=https://www.youtube.com/@PhilipTzimopou
DefaultDirName={localappdata}\Programs\Voiceboard
DefaultGroupName=Voiceboard
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=VoiceboardSetup
SetupIconFile=..\build\Voiceboard.ico
UninstallDisplayIcon={app}\Voiceboard.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
; Η εφαρμογή κλείνει πριν από αναβάθμιση ή απεγκατάσταση με το [Code] στο τέλος (taskkill): το κλείσιμο του
; παραθύρου την κρύβει στη γραμμή εργασιών, οπότε το Restart Manager των Windows δεν αρκεί.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Voiceboard when Windows starts (Άνοιγμα με την εκκίνηση των Windows)"
Name: "desktopicon"; Description: "Desktop shortcut (Συντόμευση στην επιφάνεια εργασίας)"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Voiceboard"; Filename: "{app}\Voiceboard.exe"
Name: "{autodesktop}\Voiceboard"; Filename: "{app}\Voiceboard.exe"; Tasks: desktopicon

[Registry]
; Ίδια τιμή με τον διακόπτη «Άνοιγμα με την εκκίνηση» μέσα στην εφαρμογή.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Voiceboard"; ValueData: """{app}\Voiceboard.exe"" --startup"; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\Voiceboard.exe"; Description: "Launch Voiceboard"; Flags: nowait postinstall

; Τα δεδομένα (%APPDATA%\Voiceboard: λεξιλόγιο, ιστορικό, κλειδί, μοντέλο Whisper 3 GB) μένουν μετά την απεγκατάσταση,
; ώστε μια επανεγκατάσταση να μη χρειάζεται νέο κατέβασμα. Σβήνονται χειροκίνητα από τον φάκελο.

[Code]
procedure CloseVoiceboard();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Voiceboard.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(500);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  CloseVoiceboard();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  CloseVoiceboard();
  Result := True;
end;
