@echo off
setlocal
where winget.exe >nul 2>nul
if errorlevel 1 (
  echo Windows Package Manager ^(winget^) was not found.
  echo Install VDD from its official project page: https://github.com/VirtualDrivers/Virtual-Display-Driver
  pause
  exit /b 1
)

echo This will install the third-party Virtual Display Driver from its winget package.
echo Windows may ask for administrator approval. Review the installer prompt before accepting.
echo.
winget install --id=VirtualDrivers.Virtual-Display-Driver -e --accept-source-agreements
set "result=%errorlevel%"
if not "%result%"=="0" (
  echo.
  echo VDD installation did not complete. You can use the official project page above to install it manually.
  pause
)
exit /b %result%
