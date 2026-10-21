# Brennor's Call Launcher

Standalone Windows x64 launcher for Brennor's Call. This repository contains the launcher source and Windows installer releases. The Unreal game source, packaged game releases, account backend and publisher credentials remain private.

**Current builds are local previews.** Login and gameplay use `127.0.0.1` on the computer running the launcher. Downloading this installer on another computer does not connect it to the publisher's PC. An internet realm will need a reachable HTTPS account service and UDP game-server address, followed by a new launcher configuration and installer release.

Install the Windows setup executable from [Releases](https://github.com/Scribble1/Brennors-Call-Launcher/releases). The .NET runtime is bundled. Register/sign in, create a character, and return to Play. The game downloads separately and supports independently authenticated simultaneous clients. Windows may request permission to install the Microsoft Visual C++ game prerequisite.

Game files download **directly from GitHub's CDN**, from private packaged releases. The account service checks your account/channel access and returns a temporary URL for the requested signed release file. It does not proxy the game files. Neither a GitHub account nor a publisher GitHub token is needed in the player launcher. URLs expire; sessions and tokens must never be logged. Signatures and SHA-256 verification are required before activating or executing downloaded files.

## Build

On Windows, install .NET SDK 10. Unreal Engine and Visual Studio are unnecessary for launcher builds.

```powershell
./Tools/Test-Launcher.ps1
dotnet run --project Launcher/Desktop -c Release
./Tools/Build-Launcher.ps1 -Version 0.2.4 -Installer -DeploymentConfig Launcher/Desktop/deployment.json
```

Installer packaging requires Inno Setup 6.7+ (or 7). The checked-in publisher configuration contains only public keys and endpoints. Never add private signing keys, API tokens or credentials. Saved game sessions use Windows DPAPI in `%LOCALAPPDATA%/BrennorsCall/Launcher`, outside application version folders.

`Launcher/Core` handles signed manifests, resumable downloads, safe version activation, repair, accounts and per-process secure launch IPC. `Launcher/Desktop` contains WPF/MVVM UI. `Launcher/UpdateHost` installs verified launcher updates after the old process exits. `Launcher/ReleaseTool` signs release manifests. The loopback `DistributionHost` is for local static-host testing; it is not used for private GitHub CDN downloads.

The Windows launcher workflow tests the updater, signatures, path validation, DPAPI accounts, real independent IPC worker processes and WPF startup, then packages a test installer. Packaged Unreal authentication/playability acceptance is performed in the private game repository with the installed Unreal toolchain.

Game updates stage into a separate version directory, verify all files, then change the active pointer. Existing clients retain the files they use. Closing the launcher leaves clients running. Settings and encrypted saved accounts survive launcher updates.

This source is synchronized from the maintained launcher subtree in the private game repository using a tracked-file allowlist. Do not copy the entire game repository into this public repository.
