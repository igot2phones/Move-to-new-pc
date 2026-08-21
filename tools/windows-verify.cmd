@echo off
setlocal enabledelayedexpansion
REM ===================================================================================
REM  MoveToNewPC - Windows verification run
REM
REM  Plain batch on purpose: this has to run on everything from Vista SP2 to Windows 11
REM  with no PowerShell version dependency, which is the same range the product targets.
REM
REM  Usage (from an ELEVATED command prompt, at the repo root):
REM      tools\windows-verify.cmd
REM      tools\windows-verify.cmd --skip-gui      (do not launch the GUI)
REM      tools\windows-verify.cmd --skip-tree     (do not build the nasty test tree)
REM
REM  Everything is written to verify-results\<timestamp>\ and summarised at the end.
REM  Exit code: 0 = all tests passed, 1 = something failed, 2 = could not run.
REM ===================================================================================

set ROOT=%~dp0..
pushd "%ROOT%"

set SKIP_GUI=0
set SKIP_TREE=0
:parseargs
if "%~1"=="" goto endargs
if /i "%~1"=="--skip-gui"  set SKIP_GUI=1
if /i "%~1"=="--skip-tree" set SKIP_TREE=1
shift
goto parseargs
:endargs

REM ---- timestamped output folder (locale-independent) -------------------------------
for /f "tokens=2 delims==" %%I in ('wmic os get localdatetime /value 2^>nul') do set LDT=%%I
if "%LDT%"=="" (
    REM wmic is absent on some Windows 11 builds; fall back to something unique.
    set STAMP=run-%RANDOM%%RANDOM%
) else (
    set STAMP=%LDT:~0,8%-%LDT:~8,6%
)
set OUTDIR=%ROOT%\verify-results\%STAMP%
mkdir "%OUTDIR%" 2>nul
set SUMMARY=%OUTDIR%\SUMMARY.txt

call :both "==================================================================="
call :both " MoveToNewPC - Windows verification"
call :both " started %DATE% %TIME%"
call :both "==================================================================="
call :both ""

REM ---- 1. environment ---------------------------------------------------------------
call :both "-- Environment ----------------------------------------------------"
ver                                                     >> "%SUMMARY%" 2>&1
call :both "Processor architecture: %PROCESSOR_ARCHITECTURE%"
call :both "Computer name:          %COMPUTERNAME%"

REM Elevation: fltmc exists on Vista+ and fails without administrator rights.
fltmc >nul 2>&1
if errorlevel 1 (
    call :both ""
    call :both "*******************************************************************"
    call :both " NOT RUNNING AS ADMINISTRATOR."
    call :both ""
    call :both " Close this window, right-click Command Prompt, choose"
    call :both " 'Run as administrator', cd back to this folder and try again."
    call :both ""
    call :both " The app and its tests carry requireAdministrator in their"
    call :both " manifests: without elevation they cannot even start, and the"
    call :both " registry-hive and other-user-profile tests are meaningless."
    call :both "*******************************************************************"
    popd
    exit /b 2
)
call :both "Elevated:               yes"

REM ---- 2. .NET Framework 4.x present? -----------------------------------------------
set NETFOUND=0
if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\clr.dll" set NETFOUND=1
if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\clr.dll" set NETFOUND=1
if "%NETFOUND%"=="0" (
    call :both ""
    call :both "*** .NET Framework 4.0 runtime NOT FOUND. ***"
    call :both "    On Vista/7 install .NET Framework 4.0 (or 4.5+) first."
    call :both "    On Windows 8 and later it is in the box, so this is unexpected."
    popd
    exit /b 2
)
call :both ".NET Framework 4.x:     present"
call :both ""

REM ---- 3. build if the binaries are missing -----------------------------------------
if not exist "%ROOT%\build\MoveToNewPC.Tests.exe" (
    call :both "-- Building (binaries not present) --------------------------------"
    call "%ROOT%\build.cmd" Release > "%OUTDIR%\build.log" 2>&1
    if errorlevel 1 (
        call :both "BUILD FAILED - see build.log"
        type "%OUTDIR%\build.log" >> "%SUMMARY%"
        popd
        exit /b 2
    )
    call :both "Build OK"
    call :both ""
)

REM ---- 4. the test suite (the important part) ---------------------------------------
call :both "-- Test suite -----------------------------------------------------"
call :both "Running MoveToNewPC.Tests.exe ..."
"%ROOT%\build\MoveToNewPC.Tests.exe" --no-pause > "%OUTDIR%\tests.txt" 2>&1
set TESTRC=%ERRORLEVEL%

REM Echo the whole thing so the agent reading this transcript sees every case.
type "%OUTDIR%\tests.txt" >> "%SUMMARY%"
type "%OUTDIR%\tests.txt"

call :both ""
if "%TESTRC%"=="0" (
    call :both "TEST RESULT: ALL PASSED  (exit code 0)"
) else (
    call :both "TEST RESULT: FAILURES PRESENT  (exit code %TESTRC%)"
)
call :both ""

REM ---- 5. the nasty tree ------------------------------------------------------------
if "%SKIP_TREE%"=="1" goto skiptree
call :both "-- Nasty test tree ------------------------------------------------"
set TREE=%OUTDIR%\nasty-tree
"%ROOT%\build\MakeTestTree.exe" "%TREE%" --big > "%OUTDIR%\maketesttree.txt" 2>&1
set TREERC=%ERRORLEVEL%
type "%OUTDIR%\maketesttree.txt" >> "%SUMMARY%"
type "%OUTDIR%\maketesttree.txt"
if "%TREERC%"=="0" (
    call :both "TEST TREE: created at %TREE%"
) else (
    call :both "TEST TREE: finished with problems (exit code %TREERC%) - see maketesttree.txt"
)
call :both ""
:skiptree

REM ---- 6. does the GUI actually start? ----------------------------------------------
if "%SKIP_GUI%"=="1" goto skipgui
call :both "-- GUI smoke test -------------------------------------------------"
call :both "Launching MoveToNewPC.exe for 8 seconds..."
start "" "%ROOT%\build\MoveToNewPC.exe"
REM ping is the portable sleep; timeout.exe does not exist on Vista.
ping -n 9 127.0.0.1 >nul
tasklist /fi "IMAGENAME eq MoveToNewPC.exe" | find /i "MoveToNewPC.exe" >nul
if errorlevel 1 (
    call :both "GUI: FAILED - the process is not running after 8 seconds."
    call :both "     It either crashed at startup or never created its window."
    call :both "     Check MoveToNewPC.log next to the EXE."
) else (
    call :both "GUI: OK - process is alive; window should show the role picker."
    taskkill /f /im MoveToNewPC.exe >nul 2>&1
    call :both "GUI: closed again."
)
call :both ""
:skipgui

REM ---- 7. collect the app's own log -------------------------------------------------
if exist "%ROOT%\build\MoveToNewPC.log" copy /y "%ROOT%\build\MoveToNewPC.log" "%OUTDIR%\MoveToNewPC.log" >nul 2>&1
if exist "%LOCALAPPDATA%\MoveToNewPC\MoveToNewPC.log" copy /y "%LOCALAPPDATA%\MoveToNewPC\MoveToNewPC.log" "%OUTDIR%\MoveToNewPC-localappdata.log" >nul 2>&1

call :both "-- Where everything went ------------------------------------------"
call :both "Results folder: %OUTDIR%"
call :both "  SUMMARY.txt          this transcript"
call :both "  tests.txt            full test output"
if "%SKIP_TREE%"=="0" call :both "  maketesttree.txt     nasty-tree generator output"
call :both "  MoveToNewPC*.log     the application log, if one was produced"
call :both ""
call :both "finished %DATE% %TIME%"

popd
if "%TESTRC%"=="0" exit /b 0
exit /b 1

REM ---- helper: write a line to the console AND the summary file ---------------------
:both
REM "echo(" is the robust form: it prints an empty line correctly and cannot be confused
REM with "echo." on odd inputs. The redirection goes FIRST because "echo text>>file" is
REM parsed as a stream redirect when text ends in a digit - and timestamps end in digits.
echo(%~1
>>"%SUMMARY%" echo(%~1
goto :eof
