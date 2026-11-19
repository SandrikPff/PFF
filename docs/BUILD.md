# Сборка PFF

## Требования

- Windows 11 x64;
- .NET SDK 8;
- Inno Setup 6.x (для установщика);
- доступ к NuGet для восстановления пакетов.

## Сборка приложения

В корне репозитория выполните в PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

Создаётся `publish\PFF.exe` — **приложение**, не установщик. Для работы аудиофункций могут потребоваться компоненты, перечисленные в документации.

## Сборка установщика

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Setup
```

Результат: `Installer\output\PFF_Setup.exe`. Если Inno Setup не найден, `build.ps1` пытается установить его через winget. При нестандартной установке укажите `-InnoCompiler "C:\...\ISCC.exe"`.

## Проверка логики маршрутизации

```powershell
dotnet run --project .\Tests\Routing\Routing.csproj -c Release
```

## Выпуск через GitHub Actions

Workflow `.github/workflows/release.yml` запускается при отправке тега формата `v*` или вручную из Actions. Он выполняет проверки, собирает установщик на Windows и публикует **`PFF_Setup.exe`** и отдельный **`PFF.exe`** в GitHub Releases (если сборка успешна). Готовые файлы публикуются без хранения бинарников в Git.

Workflow зависит от наличия NuGet, Chocolatey, официальных загрузок зависимостей и GitHub Actions. При сбое сборки релиз не считается готовым. Загрузочный файл `PFF.exe` — самостоятельное приложение, `PFF_Setup.exe` — установщик для конечных пользователей.
