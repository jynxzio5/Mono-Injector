# Mono Injector

Open-source injector used with the Gamble With Your Friends menu.

## What it is

This repository contains the injector source code used to load the menu DLL into the game process. The injector is written in C# and uses WinAPI calls to open the game process, copy the menu DLL into the game-managed folder, and trigger the loader entry point.

## Included Files

- `MonoInjector.cs`: main injector source.
- `build_injector.ps1`: builds the injector executable with `csc.exe`.
- `inspect.cs`: helper utility for checking assembly loading and type names.

## Build

Use `build_injector.ps1` from a Windows machine with the .NET Framework compiler available at the path expected by the script.

The script builds `MonoInjector.exe` from `MonoInjector.cs`.

## Usage

1. Build the menu DLL first.
2. Build the injector executable.
3. Run the injector as administrator.
4. Launch the game or let the injector wait for it.

## Notes

- The repository intentionally contains source files only.
- Built binaries are not required in the open-source repo.
- This project is licensed under the MIT License. See [LICENSE](LICENSE) for details.
