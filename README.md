# AI Usage Monitor

Malý přenosný widget pro Windows, který zobrazuje aktuální využití limitů **Claude Code** a **Codexu** i předplacený zůstatek na **OpenRouteru** a **nano-gpt.com**. Běží v oznamovací oblasti, lze jej nechat vždy nahoře a jeho nastavení jsou vedle EXE.

Aktuální instalační soubor je vždy v [GitHub Releases](https://github.com/jpribil/ai-usage/releases). Projekt je určen pro Windows x64.

## Co umí

- Zobrazuje 5hodinový a sedmidenní limit Claude Code včetně času resetu.
- Zobrazuje sedmidenní limit Codexu a případné zbývající resety.
- Ukazuje dolarový zůstatek OpenRouteru a nano-gpt.com.
- Kliknutí na zůstatek routeru otevře přímo jeho stránku s kredity; kurzor nad odkazem je ručička.
- Obnovuje údaje po 1, 5, 15 nebo 60 minutách, případně ručně z menu.
- Umí upozornit přes [ntfy.sh](https://ntfy.sh), když vybraný limit dosáhne 100 %.
- Podporuje češtinu, angličtinu, světlý/tmavý motiv, automatické spuštění s Windows a režim vždy nahoře.
- Kontroluje nové vydání v tomto soukromém GitHub repozitáři.

## Odkud se berou údaje

Widget data nesestavuje odhadem a neposílá je přes žádný vlastní server. Čte je přímo z účtu a API poskytovatele na počítači uživatele.

| Služba | Co widget ukazuje | Zdroj |
| --- | --- | --- |
| Claude Code | Využití 5 h a 7 dní, čas resetu | Přihlašovací token Claude Code z `%USERPROFILE%\\.claude\\.credentials.json`, případně z nalezené WSL distribuce; dotaz na Anthropic OAuth usage endpoint. Pokud hlavní odpověď limity neobsahuje, aplikace přečte rate-limit hlavičky odpovědi Anthropic API. |
| Codex | Využití krátkého a týdenního okna, čas resetu, zbývající resety | Přihlašovací token Codexu z `%USERPROFILE%\\.codex\\auth.json` (nebo z `CODEX_HOME\\auth.json`); dotaz na usage endpoint účtu ChatGPT/Codex. |
| OpenRouter | Zbývající předplacené USD | API `GET /api/v1/credits`: aplikace počítá `total_credits − total_usage`. Je nutný **management API key** OpenRouteru. |
| nano-gpt.com | Aktuální USD zůstatek | API `POST /api/check-balance` s hlavičkou `x-api-key`; je nutný API klíč nano-gpt.com. |

Zůstatek OpenRouteru otevře [nastavení kreditů](https://openrouter.ai/settings/credits), nano-gpt.com otevře [stránku zůstatku](https://nano-gpt.com/balance).

## Použití

1. Stáhněte `AIUsageMonitor.exe` z [Releases](https://github.com/jpribil/ai-usage/releases) a uložte jej do vlastní složky.
2. Ujistěte se, že je nainstalovaný **.NET 8 Windows Desktop Runtime (x64)**. EXE je záměrně framework-dependent, aby byl malý.
3. Spusťte aplikaci. Claude Code a Codex fungují, pokud jste v odpovídajícím CLI již přihlášeni.
4. Klikněte pravým tlačítkem na widget a v položce **API klíče routerů…** zadejte klíč OpenRouteru a/nebo nano-gpt.com.

Pravé tlačítko otevře celé menu: ruční obnovení, interval, vzhled, jazyk, upozornění, start s Windows, kontrolu aktualizací a ukončení aplikace. Dvojklik na ikonu v oznamovací oblasti widget schová nebo znovu zobrazí.

## Soukromí a soubory

- `settings.json` se vytváří **ve stejné složce jako EXE**, nikoli v AppData.
- API klíče routerů a token pro kontrolu aktualizací se před uložením šifrují pomocí Windows DPAPI pro aktuálního uživatele.
- Přihlašovací tokeny Claude Code a Codexu se pouze čtou z jejich existujících lokálních souborů; aplikace je nemění.
- Diagnostický log je v `%TEMP%\\ai-usage-monitor.log`; při chybě startu je samostatný `%TEMP%\\ai-usage-monitor-startup-errors.log`.

Pro soukromý repozitář vyžaduje kontrola aktualizací v menu GitHub token s přístupem pouze pro čtení obsahu/releasů daného repozitáře.

## Vývoj a vydání

```powershell
dotnet build .\src\AIUsageMonitor\AIUsageMonitor.csproj -c Release
.\tools\release.ps1 -Version '2.15'
```

Skript publikuje EXE do `publish\\win-x64`, ukončí pouze dříve spuštěnou kopii z této složky, aplikaci po sestavení spustí a vytvoří commit i tag. GitHub Actions připojí EXE k release.

Tento README je součástí vydání: při každé změně funkcí, zdrojů dat, oprávnění nebo instalace se aktualizuje spolu s kódem.
