# gptcheck

Windows tray app that reads local Codex session files from `%CODEX_HOME%\\sessions` or `%USERPROFILE%\\.codex\\sessions` and shows remaining allowance as digits:

- top row: remaining short-window percent
- bottom row: remaining long-window percent

It also adds a second tray icon for Claude usage from Claude Code's OAuth usage endpoint:

- Codex icon: OpenAI-green triangle in the bottom-right corner
- Claude icon: Claude-orange triangle in the bottom-right corner
- Claude icon digits: remaining 5-hour percent on top and remaining weekly percent on bottom
- Claude popup menu: exact reset times and usage source

The app uses raw Win32 tray APIs and does not depend on the Windows Desktop framework.

Publish the native executable to the current directory:

```powershell
dotnet publish .\gptcheck.csproj -c Release -r win-x64 -o . -p:PublishAot=true -p:SelfContained=true -p:InvariantGlobalization=true
```

The published binary is `.\gpt.exe`.
