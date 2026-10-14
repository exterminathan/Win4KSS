# Code signing policy

This page describes how [Win4KSS](https://github.com/exterminathan/Win4KSS) releases are built and signed. It exists to satisfy [SignPath Foundation](https://signpath.io)'s requirement that a qualifying open-source project publish its signing policy publicly, and it's linked from this project's [README](../README.md) and every [Release](https://github.com/exterminathan/Win4KSS/releases).

## Publisher

Releases are signed by **SignPath Foundation**, a nonprofit that provides free code signing to qualifying open-source projects. Win4KSS itself does not hold, store, or have direct access to the signing certificate — the private key never leaves SignPath's infrastructure.

## Roles

Win4KSS is a single-maintainer project. **exterminathan** (the repository owner) is the sole committer, reviewer, and release approver. Every signing request corresponds to a specific, reviewed commit on the `main` branch of [github.com/exterminathan/Win4KSS](https://github.com/exterminathan/Win4KSS) — nothing is signed from a fork, a branch other than `main`, or an uncommitted/local build.

## Build process

Every signed release is built entirely by **GitHub-hosted runners** (never self-hosted, never a local machine) via a GitHub Actions workflow checked into this repository at [`.github/workflows/`](../.github/workflows/). The workflow:

1. Checks out the exact tagged commit from this repository.
2. Builds the application (`dotnet publish`) and the Windows installer (Inno Setup) from source, with no manual intervention.
3. Submits the resulting installer as a signing request to SignPath, tied to that specific build artifact and commit.
4. On approval, publishes the signed installer as a GitHub Release asset on this repository.

Anyone can read the exact workflow definition that produces each release — it's not a black box.

## What gets signed

Only the Windows installer (`Win4KSS-Setup-<version>.exe`) published as a GitHub Release asset on this repository is ever submitted for signing. Nothing else — no other binary, no fork, no third-party redistribution — is signed under this policy.

## Reporting a concern

If you believe a release claiming to be signed under this policy is not what it claims to be, or you've found something that looks wrong with a signed Win4KSS release, please [open an issue](https://github.com/exterminathan/Win4KSS/issues) on this repository.

## Privacy

Win4KSS itself collects no telemetry and sends no usage data anywhere. Settings and the video cache stay entirely on your own machine. The app does make outbound network requests to stream video and refresh its clip catalogue — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for exactly which third-party sources it talks to and why. The build and signing process itself (GitHub Actions, SignPath) only ever handles the installer binary — never anything from your machine.
