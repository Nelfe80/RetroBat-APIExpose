# Troubleshooting

## APIExpose does not seem to start

1. Check the service health: `http://127.0.0.1:12345/api/v1/health`. No answer? The process is not running.
2. Is **.NET 8** installed? The installer takes care of it; otherwise install **ASP.NET Core Runtime 8** and **.NET Desktop Runtime 8** (x64) from the [.NET 8 page](https://dotnet.microsoft.com/download/dotnet/8.0), or simply run the installer again.
3. Is the hook in place? Rerun `install-es-start-hook.bat` (RetroBat closed), then restart RetroBat. The `.log\es-start-hook.log` file tells what happened at the last startup: API started, ready, .NET 8 not found, timeout.
4. Startup can take a while on a large installation (initial processing): `http://127.0.0.1:12345/api/v1/startup/ready` shows the progress.

## RetroBat stays on the startup screen for a long time

The hook makes EmulationStation wait until APIExpose is ready, two minutes at most, and does not wait at all if APIExpose cannot start. If your installation is older, its hook could wait up to ten minutes: run the installer again, or `install-es-start-hook.bat` (RetroBat closed), to update it. APIExpose's automatic update does not replace this file on the EmulationStation side.

## APIExpose menus do not appear in EmulationStation

The options install into `EXTENDED OPTIONS` and the dedicated managers during the first full startup. Restart RetroBat once APIExpose is healthy (`/health` answers).

## A pack dropped in package-installer is not imported

- Restart RetroBat: imports happen during the startup phase.
- Check the format (`.zip`, `.7z`, `.rar`) and that the archive is not corrupted.
- Check the logs (below): every processed pack leaves a trace.

## My gamelists changed and I want to roll back

APIExpose backs up before modifying: look for backup folders next to the gamelists (e.g. `.api-expose-gamelist-backups`). This is also why the golden rule remains: **full backup before first use**.

## ScreenScraper scraping does not work

Remote calls need valid credentials and ScreenScraper must be reachable. Check the configuration in the ES menu `AUTO SCRAPING MANAGER`, and remember that local scraping is always attempted first.

## Where are the logs?

In the plugin's `.log\` folder, organized by feature (pack imports, ES menus, payloads). It is the first thing to attach to a help request on GitHub.

## Still stuck?

Open a ticket on the [APIExpose issue tracker](https://github.com/Nelfe80/RetroBat-APIExpose/issues), attaching the logs from the `.log\` folder.
