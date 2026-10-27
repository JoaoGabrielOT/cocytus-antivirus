@echo off
chcp 65001 >nul
title Cocytus - db
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0cocytus.ps1" -Task db
if errorlevel 1 (
  echo.
  echo Houve falhas. Veja o log em .dev\logs e o resumo em .dev\logs\ultimo-status.txt
  pause
) else (
  echo.
  echo Concluido sem falhas. Esta janela fecha em 15 segundos.
  timeout /t 15 >nul
)
