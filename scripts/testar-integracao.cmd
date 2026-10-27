@echo off
chcp 65001 >nul
title Cocytus - teste de integracao
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0testar-integracao.ps1"
timeout /t 10 >nul
