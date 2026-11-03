@echo off
setlocal
title Another Day in GTA - set Gemini key

set "SVC=%LOCALAPPDATA%\AnotherDayInGTA\service\AnotherDay.Service.exe"
set "KEY=%LOCALAPPDATA%\AnotherDayInGTA\gemini.key"

if not exist "%SVC%" (
    echo.
    echo   The AI service is not installed yet.
    echo   Expected: "%SVC%"
    echo   Run tools\install.ps1 first.
    echo.
    pause
    exit /b 1
)

echo.
echo   Another Day in GTA - Gemini API key
echo   ------------------------------------------------
echo   Paste the key below and press Enter.
echo   Nothing is echoed while you type or paste.
echo.
"%SVC%" --set-key
echo.
if errorlevel 1 (
    echo   The key was NOT stored. Check that it is a valid key of at least 20 characters.
) else (
    echo   Done. Stored encrypted at:
    echo     "%KEY%"
    echo.
    echo   Close the game if it is running, then launch it again.
    echo   The service log should then say: Gemini: configured
)
echo.
pause
