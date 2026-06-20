# LingosBot

A Windows desktop bot that automates lessons on [lingos.pl](https://lingos.pl) using Selenium and Google Chrome. It logs in, learns your vocabulary from your *Zestawy*, and auto-completes lessons. Before each lesson it automatically picks or continues a **Wyzwania** (challenge), preferring the one worth the most points.

This repository contains the **source code** — you build the program yourself with one command (see below).

## Run it (build from source)

You need the **.NET 10 SDK** and **Google Chrome** installed.

```
# 1. get the code
git clone https://github.com/yuriiamelnychuk-lgtm/Lingos.git
cd Lingos

# 2a. run it directly
dotnet run --project LingosBot

# 2b. ...or build a standalone .exe you can double-click
dotnet publish LingosBot -c Release -r win-x64 -p:PublishSingleFile=true --self-contained true
```

The published `.exe` is **self-contained** — it bundles the .NET runtime, so once built it needs nothing but **Google Chrome** to run. (You only need the .NET SDK to *build* it, not to run it.)

## First run

1. It asks for your lingos.pl email & password (stored **encrypted** on your own PC).
2. You choose how many lessons to run.

## Menu

- `[number]` — run that many lessons (each one auto-picks/continues the highest-point challenge)
- `[R]` — change your saved email / password (with a "back" option)
- `[Q]` — quit

Chrome runs **headless** (no window) and **muted**, and each word is streamed to the terminal as it's answered.

## Notes

- Credentials are stored locally in `credentials.json` (encrypted with Windows DPAPI) and are **never** committed to this repo.
- This is a personal / educational project. Automating a third-party website may conflict with its terms of service — use responsibly and at your own risk.
