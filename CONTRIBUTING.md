# Contributing to MinerGuru

Thank you for helping. MinerGuru is a small project with one maintainer, so a few notes keep things simple.

## Issues

Bugs and ideas for all three apps go to the [issues of the Mac repository](https://github.com/GuruMeditationSoftware/minerguru/issues). Please name the app and its version, the miner model and its firmware version.

Before you post a log or a screenshot, hide IP addresses, wallet addresses, hostnames and your Wi-Fi name. The eye button in the app does this for screenshots.

## Pull requests

Welcome, above all for miner models and firmware versions MinerGuru does not know yet.

- This repository is a snapshot of each release. Development happens elsewhere, so a pull request is not merged here: the maintainer carries the change over, it ships with the next release, and the pull request is closed with a note. Your contribution is credited in the release notes.
- Keep a pull request to one topic and add or update a test for it.
- Run the tests before you open it: `dotnet test tests\MinerGuru.Core.Tests`.
- New text in the app needs German and English.
- By opening a pull request you agree that your contribution is licensed under the [MIT License](LICENSE.txt) of this project.

## Developer documentation

In German: [docs/ARCHITEKTUR.md](docs/ARCHITEKTUR.md) and, for what all three apps share, [GRUNDLAGEN.md](https://github.com/GuruMeditationSoftware/minerguru/blob/main/docs/GRUNDLAGEN.md) in the Mac repository.
