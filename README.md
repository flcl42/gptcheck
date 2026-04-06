# gptcheck

Windows tray app that reads local Codex session files from `%CODEX_HOME%\\sessions` or `%USERPROFILE%\\.codex\\sessions` and shows remaining allowance as digits:

- top row: remaining short-window percent
- bottom row: remaining long-window percent

The app uses raw Win32 tray APIs and does not depend on the Windows Desktop framework.

Publish the native executable to `C:\Programs`:

```powershell
dotnet publish .\gptcheck.csproj -c Release -r win-x64 -o C:\Programs -p:PublishAot=true -p:SelfContained=true -p:InvariantGlobalization=true
```

The published binary is `C:\Programs\gptcheck.exe`.
