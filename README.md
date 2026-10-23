# LingosBot

A desktop bot that automates lessons on [lingos.pl](https://lingos.pl) using Selenium and Google Chrome. It logs in, learns your vocabulary from your *Zestawy*, and auto-completes lessons. Before each lesson it automatically picks or continues a **Wyzwania** (challenge), preferring the one worth the most points.

This repository contains the **source code** — you build the program yourself with one command (see below).

## Run it (build from source)

You need the **.NET 10 SDK**.

```
# 1. get the code
git clone https://github.com/teaquilish/Lingos.git
cd Lingos

# 2a. run it directly
dotnet run --project LingosBot

# 2b. ...or build a standalone .exe you can double-click
dotnet publish LingosBot -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true
```

The published `.exe` is **self-contained** — it bundles the .NET runtime, so once built it needs nothing else to run. (You only need the .NET SDK to *build* it, not to run it.)

You don't have to install Chrome yourself: Selenium Manager downloads a matching Chrome and driver on first run and caches them under `%USERPROFILE%\.cache\selenium`. If you already have Chrome installed it is used as-is, and you can point the bot at a specific binary with the `LINGOS_CHROME_BINARY` environment variable.

## First run

1. It asks for your lingos.pl email & password (stored **encrypted** on your own PC).
2. You pick what to run from the menu.

## Menu

- `[number]` — run that many lessons on the class that is currently selected
- `[U]` — *Usual*: every class on your account, 5 lessons each
- `[M]` — *Modified*: every class on **both** accounts (asks for the second login the first time)
- `[R]` — change your saved email / password (with a "back" option)
- `[Q]` — quit

Every lesson auto-picks or continues the highest-point challenge first.

Chrome runs **headless** (no window) and **muted**, and each word is streamed to the terminal as it's answered.

## How it works

- Reads every wordset in the current class and builds a Polish prompt to foreign answer lookup, fresh on each run.
- Answers only from what it collected. If a prompt has no stored translation it stops rather than guessing.
- Talks to the site the same way the site's own scripts do: page state comes from the JSON lingos.pl renders into `#app[data-props]`, and answers go through its `POST /learning/answer/{lessonId}` endpoint. CSS selectors are only used to log in and move around the site.
- If lingos.pl changes something the bot relies on, it stops and says what changed (and which step it was on) instead of guessing.
- Free accounts have a daily lesson cap. When it is reached the bot reports it and stops cleanly instead of hanging.

## Notes

- Credentials are stored locally in `credentials.json` (and `credentials-2.json` for the second account), encrypted with Windows DPAPI, and are **never** committed to this repo.
- On failure the bot writes the page HTML, a screenshot and the stack trace to a `diagnostics/` folder next to the exe.
- This is a personal / educational project. Automating a third-party website may conflict with its terms of service — use responsibly and at your own risk.
