# LingosBot

A Windows desktop bot that automates lessons on [lingos.pl](https://lingos.pl) using Selenium and Google Chrome. It logs in, learns your vocabulary from your *Zestawy*, and auto-completes lessons. Before each lesson it automatically picks or continues a **Wyzwania** (challenge), preferring the one worth the most points.

## Download & run (no compiler needed)

1. Download `LingosBot.exe` from the [Releases](../../releases) page.
2. Make sure **Google Chrome** is installed.
3. Run it from a terminal so you can watch the output:
   ```powershell
   .\LingosBot.exe
   ```
4. On the first run it asks for your lingos.pl email & password (stored encrypted on your own PC).
5. Choose how many lessons to run.

The exe is **self-contained** — it bundles the .NET runtime, so you do **not** need .NET, a compiler, or anything else installed. Google Chrome is the only requirement.

## Menu

- `[number]` — run that many lessons (each one auto-picks/continues a challenge)
- `[R]` — change your saved email / password (with a "back" option)
- `[Q]` — quit

Chrome runs **headless** (no window) and **muted**, and each word is streamed to the terminal as it's answered.

## Build from source (optional)

Requires the **.NET 10 SDK**.

```powershell
# run directly
dotnet run --project LingosBot

# or produce the standalone exe
dotnet publish LingosBot -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true
```

## Notes

- Credentials are stored locally in `credentials.json` (encrypted with Windows DPAPI) and are **never** committed to this repo.
- This is a personal / educational project. Automating a third-party website may conflict with its terms of service — use responsibly and at your own risk.
