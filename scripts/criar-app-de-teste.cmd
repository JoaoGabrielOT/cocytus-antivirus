@echo off
chcp 65001 >nul
title Cocytus - criar app de teste
rem Cria um "programa instalado" falso, só no registro do usuário (HKCU), para testar a tela Desinstalar apps:
rem   - entrada em Programas e Recursos "Cocytus App de Teste" cujo desinstalador só apaga a própria entrada;
rem   - pasta %LOCALAPPDATA%\Programs\Cocytus App de Teste (com um arquivo de texto) e a chave
rem     HKCU\Software\Cocytus App de Teste, que devem aparecer como restos depois da desinstalação.
rem Não precisa de administrador e não instala nada de verdade.
set "KEY=HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\CocytusAppDeTeste"
set "DIR=%LOCALAPPDATA%\Programs\Cocytus App de Teste"
if not exist "%DIR%" mkdir "%DIR%"
echo Arquivo de teste do Cocytus. Pode ser apagado.> "%DIR%\leia-me.txt"
reg add "%KEY%" /v DisplayName /d "Cocytus App de Teste" /f >nul
reg add "%KEY%" /v Publisher /d "Projeto Cocytus" /f >nul
reg add "%KEY%" /v DisplayVersion /d "1.0" /f >nul
reg add "%KEY%" /v InstallLocation /d "%DIR%" /f >nul
reg add "%KEY%" /v UninstallString /d "\"%SystemRoot%\System32\reg.exe\" delete \"%KEY%\" /f" /f >nul
reg add "HKCU\Software\Cocytus App de Teste" /v Configuracao /d "teste" /f >nul
echo Criado: "Cocytus App de Teste" (Programas e Recursos), "%DIR%" e HKCU\Software\Cocytus App de Teste.
echo Abra o Cocytus, tela Desinstalar apps, e desinstale "Cocytus App de Teste".
timeout /t 15 >nul
