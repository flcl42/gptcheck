# gptcheck

Windows tray app that reads local Codex session files from `%CODEX_HOME%\\sessions` or `%USERPROFILE%\\.codex\\sessions` and shows remaining allowance as digits:

- top row: remaining short-window percent
- bottom row: remaining long-window percent

It also adds a second tray icon for Claude usage. Claude status is read from
Claude Code's OAuth usage metadata endpoint. It does not invoke
`claude -p /usage` and does not write a usage state file:

- Codex icon: OpenAI-green triangle in the bottom-right corner
- Claude icon: Claude-orange triangle in the bottom-right corner
- Claude icon digits: remaining 5-hour percent on top and remaining weekly percent on bottom
- Claude popup menu: exact reset times and usage source

The same `gpt.exe` process also performs the old limits watchdog work: it
monitors C: and D: free space and runs the pause/resume batch files when Claude
or disk thresholds cross.

The app uses raw Win32 tray APIs and does not depend on the Windows Desktop framework.

Download the latest released executable to the current directory:

```powershell
iwr https://github.com/flcl42/gptcheck/releases/latest/download/gpt.exe -OutFile .\gpt.exe
```

Publish the native executable to the current directory:

```powershell
dotnet publish .\gptcheck.csproj -c Release -r win-x64 -o . -p:PublishAot=true -p:SelfContained=true -p:InvariantGlobalization=true
```

The published binary is `.\gpt.exe`.

Publish the native executable to `C:\Programs`:

```powershell
.\install.ps1
```

The installer publishes to `publish\gpt`, copies only `gpt.exe` into
`C:\Programs`, and starts it. The installed binary is `C:\Programs\gpt.exe`.
