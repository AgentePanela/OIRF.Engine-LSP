@echo off

dotnet publish server/src/OIRF.LanguageServer -c Release -o client/dist/server
if errorlevel 1 exit /b %errorlevel%

cd client
call npx vsce package

pause