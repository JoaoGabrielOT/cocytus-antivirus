@echo off
rem Abre o build de dist\Cocytus usando os dados de desenvolvimento em .dev\data
set "COCYTUS_DATA=%~dp0..\.dev\data"
start "" "%~dp0..\dist\Cocytus\Cocytus.exe"
