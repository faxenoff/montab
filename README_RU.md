# montab

[EN](README.md) | **RU**

Боковая панель-таскбар для Windows с **постоянными живыми превью** всех открытых
окон. Док слева или справа, резервирует рабочую область (развёрнутые окна не
перекрывают панель), обновление превью — в реальном времени через DWM-композитор,
практически бесплатно по ресурсам.

Один exe **1,95 МБ** (.NET 11 NativeAOT, ноль зависимостей), ~4 МБ собственной
памяти, ~0% CPU в простое.

## Возможности

- Живые превью всех окон со всех мониторов, аспект сохраняется.
- Лента из двух секций: сверху живые превью (новые окна — в самый верх),
  снизу — свёрнутые окна компактными полосками с иконкой и названием.
  Свернувшееся окно встаёт первым среди полосок, развернувшееся — последним
  среди живых.
- Активное окно подсвечено рамкой, его превью затенено.
- После сворачивания фокус уходит в последнее по истории открытое окно
  (свёрнутые пропускаются).
- Виртуализация: превью за пределами видимости не потребляют ресурсы.
- Панель помнит монитор, край и ширину между запусками
  (`%APPDATA%\montab\settings.json`).

## Управление

| Действие | Результат |
|---|---|
| Клик по живому превью | Переключение в окно (задержка ~150 мс — отличение от двойного клика) |
| Клик по полоске | Мгновенный restore + переключение (двойной клик делает то же) |
| Двойной клик по живому превью (в любом месте) | Свернуть окно (системный minimize) в полоску |
| Клик по ✕ в заголовке | Закрыть приложение |
| Колесо мыши | Прокрутка ленты |
| Наведение на превью (~0,7 с) | Временная лупа ×5, движение мыши панорамирует; уход с превью — возврат |
| Ctrl + колесо над превью | Постоянный zoom ×1–5 |
| Ctrl + движение мыши | Панорамирование увеличенного превью |
| Ctrl + клик | Сброс zoom/pan |
| Перетаскивание превью | Изменение порядка (в пределах своей секции; таскаемый элемент подсвечен) |
| Перетаскивание за «ручку» сверху или пустую зону | Перенос панели на другой монитор/край (край — по половине монитора) |
| Перетаскивание внутреннего края | Ширина панели (3–20% ширины монитора) |
| Правый клик | Меню: край дока, автозапуск, выход |

## Браузер замирает в превью?

Chromium-браузеры (Chrome, Brave, Edge) отслеживают перекрытие своего окна
(«native window occlusion»): как только окно полностью закрыто другими,
браузер перестаёт рендерить кадры — звук играет, а превью в панели замирает
на последнем кадре. Это не ограничение montab: DWM показывает только то, что
приложение само отрисовало.

Лечится штатной политикой браузера — выполните в PowerShell **одну** команду
под свой браузер и перезапустите его:

```powershell
# Brave
New-Item 'HKCU:\Software\Policies\BraveSoftware\Brave' -Force | Out-Null
New-ItemProperty 'HKCU:\Software\Policies\BraveSoftware\Brave' `
  -Name WindowOcclusionEnabled -Value 0 -PropertyType DWord -Force | Out-Null

# Chrome
New-Item 'HKCU:\Software\Policies\Google\Chrome' -Force | Out-Null
New-ItemProperty 'HKCU:\Software\Policies\Google\Chrome' `
  -Name WindowOcclusionEnabled -Value 0 -PropertyType DWord -Force | Out-Null

# Edge
New-Item 'HKCU:\Software\Policies\Microsoft\Edge' -Force | Out-Null
New-ItemProperty 'HKCU:\Software\Policies\Microsoft\Edge' `
  -Name WindowOcclusionEnabled -Value 0 -PropertyType DWord -Force | Out-Null
```

(Старая политика `NativeWindowOcclusionEnabled` устарела и удалена из
Chromium — показывается как «Unknown policy»; `WindowOcclusionEnabled` — её
замена.)

Проверить, что политика применилась: `brave://policy` (или `chrome://policy`,
`edge://policy`) — там должна появиться `WindowOcclusionEnabled: 0` без ошибок.
Откат — удалить значение:
`Remove-ItemProperty 'HKCU:\Software\Policies\BraveSoftware\Brave' -Name WindowOcclusionEnabled`.

Альтернатива без реестра — ключи в ярлыке браузера:
`--disable-features=CalculateNativeWinOcclusion --disable-backgrounding-occluded-windows`.

Цена: полностью перекрытый браузер продолжает тратить GPU/CPU на отрисовку.
На свёрнутые окна не влияет (они в панели и так полоски).

## Сборка

Требуется .NET 11 SDK (сейчас — preview 5+) и MSVC (для NativeAOT-линковки). Windows 10 1809+.

```powershell
dotnet build                           # dev-сборка
dotnet publish -c Release -r win-x64   # один exe ~2 МБ
```

Release-сборка обрезана жёстко: без стектрейсов и текстов исключений
(`StackTraceSupport=false`, `UseSystemResourceKeys=true`) — для отладки
используйте dev-сборку.

Если линковка падает с ошибкой про `vswhere.exe`, добавьте в PATH:
`C:\Program Files (x86)\Microsoft Visual Studio\Installer`.

## Технологии

Чистый Win32 (без WPF/WinUI): DWM Thumbnail API (превью — композитор отдаёт
кадры окон без захвата и кодирования), AppBar API (резервирование рабочей
области), SetWinEventHook (трекинг окон без поллинга), GDI с кешированным
backbuffer (отрисовка), CsWin32 (P/Invoke source generator, без маршалинга),
Per-Monitor V2 DPI. .NET 11 + C# 15, NativeAOT. В стабильном кадре — ноль
аллокаций. Подробности — в [PLAN_RU.md](PLAN_RU.md).

## Лицензия

[MIT](LICENSE).
