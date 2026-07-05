# Troubleshooting

## APIExpose does not seem to start

1. Check the service health: `http://127.0.0.1:12345/api/v1/health`. No answer? The process is not running.
2. Is the **.NET 8 runtime** installed?
3. Is the hook in place? Rerun `install-es-start-hook.bat` (RetroBat closed), then restart RetroBat.
4. Startup can take a while on a large installation (initial processing): `http://127.0.0.1:12345/api/v1/startup/ready` shows the progress.

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
