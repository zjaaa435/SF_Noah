## BugLens Lite (C# / WinForms / WebView2)

This is a **Windows desktop** prototype to avoid Node.js for end users.

### Goal (prototype)

- UI similar to BugLens (left bug list, center table, bottom tabs)
- **TT/Noah login** inside embedded WebView2
- Extract `SIAMTGT` (value like `TGT-...`) from cookies
- Use `Authorization: Bearer <SIAMTGT>` to call:
  - `GET https://g-agile-dms.myoas.com/api/dms/api/landray/process/workItem?collection=OPPO&workItem=<bugId>`
  - Parse `Oppo.LogInfo` / description to find `poseidon.../share/file/<fileId>`
  - `GET <poseidonSuffix>api/poseidon-service/shareFile/<fileId>` to get storageKeys
  - `GET <poseidonSuffix>api/poseidon-service/shareFile/download/preSignedUrl/<storageKey>` to get download URLs

### Requirements (developer)

- Windows
- .NET SDK 8.0+
- (Runtime) Microsoft Edge WebView2 Runtime (usually already installed)

### Build (developer)

```powershell
cd buglens_lite_cs\BugLensLite
dotnet restore
dotnet run
```

### Publish (for users, no Node)

```powershell
cd buglens_lite_cs\BugLensLite
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:SelfContained=true
```

Output is under:

`bin\Release\net8.0-windows\win-x64\publish\`






