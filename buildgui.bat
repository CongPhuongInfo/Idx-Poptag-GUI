@echo off
setlocal enabledelayedexpansion

set "FRAMEWORK_BASE=C:\Windows\Microsoft.NET\Framework64"
set "CSC="

for %%V in (4.8 4.7.2 4.7.1 4.7 4.6.2 4.6.1 4.6 4.5.2 4.5.1 4.5 4.0) do (
    if "!CSC!"=="" (
        for /d %%D in ("%FRAMEWORK_BASE%\v%%V*") do (
            if exist "%%D\csc.exe" set "CSC=%%D\csc.exe"
        )
    )
)

if "%CSC%"=="" (
    set "FRAMEWORK_BASE=C:\Windows\Microsoft.NET\Framework"
    for %%V in (4.8 4.7.2 4.7.1 4.7 4.6.2 4.6.1 4.6 4.5.2 4.5.1 4.5 4.0) do (
        if "!CSC!"=="" (
            for /d %%D in ("%FRAMEWORK_BASE%\v%%V*") do (
                if exist "%%D\csc.exe" set "CSC=%%D\csc.exe"
            )
        )
    )
)

if "!CSC!"=="" (
    echo [ERROR] Khong tim thay csc.exe
    exit /b 1
)

echo [INFO] Compiler: !CSC!

"!CSC!" ^
  /target:winexe ^
  /optimize+ ^
  /platform:x86 ^
  /out:%cd%\EmoTE_GUI.exe ^
  /r:System.Windows.Forms.dll ^
  /r:System.Drawing.dll ^
  %cd%\EmoTE_Extractor_lib.cs ^
  %cd%\EmoTE_GUI.cs

if errorlevel 1 (
    echo [ERROR] Build that bai!
) else (
    echo [OK] Build thanh cong: EmoTE_GUI.exe
)
endlocal
