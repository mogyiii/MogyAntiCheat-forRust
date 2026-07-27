# Building & Distributing MogyAntiCheat as a Precompiled DLL

By default MogyAntiCheat ships as a single `.cs` file that Oxide/Carbon compiles at runtime.
This guide covers building it **ahead of time into a `.dll`** — e.g. for IP/tamper protection.

> **Important — this is not a substitute for disclosure.** The plugin's data-collection notice
> (`docs/DATA_COLLECTION.md`), the on-load console disclosure, and the opt-in `WeeklyReport.Accepted`
> flag must remain intact in any DLL build. Shipping a binary specifically to hide telemetry from
> server operators is a backdoor and will destroy trust the moment anyone decompiles the DLL
> (a .NET assembly is trivial to decompile). Ship the DLL for convenience/protection, **not** to
> conceal the weekly report.

> **Webhook is not in the source.** The public source keeps `DefaultWeeklyReportWebhook =
> "__WEEKLY_WEBHOOK__"` (a sentinel), so `.cs`/source deployments have **no** default webhook and send
> nothing. The real webhook is injected only into the **release DLL** at build time (see
> [Injecting the release webhook](#injecting-the-release-webhook)). Forks should use their own webhook
> (or none) and keep `docs/DATA_COLLECTION.md` accurate for their users.

## Reality check per framework

| Framework | Precompiled DLL support | Effort |
|-----------|-------------------------|--------|
| **Carbon** | Yes — native support for compiled plugin assemblies | Low |
| **Oxide/uMod** | Not for regular plugins; the DLL route means converting to an **Oxide Extension** | High |

The officially supported and simplest path on Oxide remains the runtime-compiled `.cs`. If you only
need a binary on **Carbon**, this is straightforward. On **Oxide**, prefer shipping the `.cs` unless
you truly need a binary, in which case see the Extension note below.

Exact folder names and reference assemblies vary between Oxide/Carbon versions — verify against the
current uMod / Carbon documentation for your server build.

## Prerequisites

- **.NET SDK** (or MSBuild / Mono `mcs`) capable of targeting **.NET Framework 4.8** (compatible
  with the 4.6+ runtime Oxide/Carbon use). Do **not** target .NET Core/5+.
- A **Rust dedicated server install** to source the managed assemblies from:
  `RustDedicated_Data/Managed/`.
- The Oxide **or** Carbon assemblies for the framework you target.
- **No NuGet packages** — reference only the assemblies already provided by the game runtime
  (`Newtonsoft.Json.dll` is provided by the server; do not bundle your own copy).

## Reference assemblies

Typical references (paths under your server's `RustDedicated_Data/Managed/`):

- `Assembly-CSharp.dll`, `Assembly-CSharp-firstpass.dll`
- `Facepunch.System.dll`, `Facepunch.Network.dll`, `Facepunch.UnityEngine.dll`
- `UnityEngine.dll`, `UnityEngine.CoreModule.dll`, `UnityEngine.PhysicsModule.dll`
- `Rust.Global.dll`, `Rust.Data.dll`
- `Newtonsoft.Json.dll`
- `System.dll`, `System.Core.dll` (for `System.Security.Cryptography`, LINQ)

Framework-specific:

- **Oxide:** `Oxide.Core.dll`, `Oxide.Rust.dll`, `Oxide.CSharp.dll`, `Oxide.References.dll`
- **Carbon:** `Carbon.Common.dll`, `Carbon.Common.Client.dll`, plus the same Rust/Unity managed set

> `System.Security.Cryptography` (HMAC-SHA256) and `System.Text` are part of the base class library
> already available at runtime — no extra package is needed for the salt/hash code.

## Example `.csproj`

Adjust `RustManaged` to your server's Managed folder.

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <AssemblyName>MogyAntiCheat</AssemblyName>
    <LangVersion>7.3</LangVersion>            <!-- match Oxide/Carbon runtime C# support -->
    <Nullable>disable</Nullable>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
    <RustManaged>C:\rustserver\RustDedicated_Data\Managed</RustManaged>
  </PropertyGroup>

  <ItemGroup>
    <!-- Rust / Unity -->
    <Reference Include="Assembly-CSharp"><HintPath>$(RustManaged)\Assembly-CSharp.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Facepunch.System"><HintPath>$(RustManaged)\Facepunch.System.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine"><HintPath>$(RustManaged)\UnityEngine.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="UnityEngine.CoreModule"><HintPath>$(RustManaged)\UnityEngine.CoreModule.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Rust.Global"><HintPath>$(RustManaged)\Rust.Global.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Newtonsoft.Json"><HintPath>$(RustManaged)\Newtonsoft.Json.dll</HintPath><Private>false</Private></Reference>

    <!-- Oxide (comment out and use Carbon refs instead if targeting Carbon) -->
    <Reference Include="Oxide.Core"><HintPath>$(RustManaged)\Oxide.Core.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Oxide.Rust"><HintPath>$(RustManaged)\Oxide.Rust.dll</HintPath><Private>false</Private></Reference>
    <Reference Include="Oxide.CSharp"><HintPath>$(RustManaged)\Oxide.CSharp.dll</HintPath><Private>false</Private></Reference>

    <!-- Carbon (uncomment when targeting Carbon)
    <Reference Include="Carbon.Common"><HintPath>C:\rustserver\carbon\managed\Carbon.Common.dll</HintPath><Private>false</Private></Reference>
    -->
  </ItemGroup>

  <ItemGroup>
    <Compile Include="MogyAntiCheat.cs" />
  </ItemGroup>

</Project>
```

`<Private>false</Private>` (a.k.a. "Copy Local = false") keeps the referenced game/framework
assemblies **out** of your output folder — you ship only `MogyAntiCheat.dll`.

Point `<Compile Include>` at the **injected copy** (`build/MogyAntiCheat.cs`, see below) when building an
official release, or at the tracked `MogyAntiCheat.cs` for a webhook-less build.

## Injecting the release webhook

The tracked source never contains the webhook — it holds the sentinel `__WEEKLY_WEBHOOK__`. To build an
official release that reports to your webhook, inject it first with `build-release.ps1`:

```powershell
# Option A: pass it directly
.\build-release.ps1 -Webhook "https://discord.com/api/webhooks/xxx/yyy"

# Option B: environment variable
$env:MOGYAC_WEEKLY_WEBHOOK = "https://discord.com/api/webhooks/xxx/yyy"
.\build-release.ps1

# Option C: a gitignored secret file
Set-Content webhook.secret "https://discord.com/api/webhooks/xxx/yyy"
.\build-release.ps1
```

This writes `build/MogyAntiCheat.cs` with the sentinel replaced. Compile **that** file into the DLL.
`build/` and `webhook.secret` are gitignored — never commit them.

> This only hides the webhook from **automated source scanners**. A `.dll` string constant is still
> readable (`strings MogyAntiCheat.dll` / any decompiler), so treat this as spam mitigation, not
> secrecy. The data-collection disclosure and opt-in flag remain required regardless.

## Build

```powershell
# From the folder containing the .csproj (Compile pointed at build\MogyAntiCheat.cs for a release)
dotnet build -c Release
# Output: bin\Release\MogyAntiCheat.dll
```

Alternatives: `msbuild /p:Configuration=Release`, or Mono `mcs` with explicit `-r:` references.

## Deploying the DLL

### Carbon (recommended for binary distribution)

1. Stop the server (or prepare to reload).
2. Copy `MogyAntiCheat.dll` into Carbon's compiled-plugin location for your Carbon version
   (commonly `carbon/plugins` alongside `.cs` plugins, or the extensions folder — check your
   Carbon build's docs).
3. Start/reload. Carbon loads the precompiled assembly and runs the standard plugin lifecycle
   (`Init`, hooks, commands) exactly as the `.cs` version.

### Oxide/uMod (Extension route — advanced)

Standard Oxide **plugins** cannot be dropped in as opaque DLLs; only `.cs` plugins are hot-compiled.
To ship a binary on Oxide you must repackage the code as an **Oxide Extension**:

- Create an `Oxide.Ext.MogyAntiCheat` project whose entry class derives from `Oxide.Core.Extensions.Extension`.
- Register the plugin from the extension's load path, or embed the plugin and load it via the
  extension.
- Name the assembly `Oxide.Ext.*.dll` and place it in `RustDedicated_Data/Managed/` (or
  `oxide/extensions/`, depending on version).

This is a non-trivial restructuring and is only worth it if a binary on Oxide is a hard requirement.
Otherwise, keep distributing the `.cs` file on Oxide.

## Notes

- **Localization still works.** The plugin embeds English/Hungarian fallback strings
  (`MessagesEn` / `MessagesHu`), so a DLL build functions even if the `oxide/lang/*` JSON files are
  absent. Ship the JSON files too if you want operators to edit wording.
- **Config, data, salt, and report state** are created at runtime as usual
  (`MogyAntiCheat.json`, `MogyAntiCheat_Stats.json`, `MogyAntiCheat_Salt.json`,
  `MogyAntiCheat_WeeklyReport.json`).
- **Reloading:** replace the DLL and reload the plugin (no runtime recompile happens for a binary,
  so the on-disk DLL is authoritative).
- **Keep the version in sync** — the `[Info(..., "x.y.z")]` attribute is baked into the DLL at
  build time; rebuild after bumping it.
