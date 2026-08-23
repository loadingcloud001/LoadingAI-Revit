# Plugins — Opensource (MIT)

This folder documents the runtime plugin directory, not the repo source.

Runtime location: `%AppData%\RevitAiPoc\Plugins\<pluginId>\plugin.json + *.dll`
Dev location (optional, copied on build if exists): `src/RevitAiPoc/Plugins/dev/<pluginId>/`

Each plugin folder example:

```
Plugins/hello/
  plugin.json
  RevitAiPoc.Plugins.Hello.dll
```

`plugin.json` schema:

```json
{
  "id": "hello",
  "name": "Hello",
  "version": "0.1.0",
  "license": "MIT",
  "author": "Community",
  "entryAssembly": "RevitAiPoc.Plugins.Hello.dll",
  "tools": [
    {"name":"hello","tier":"L1","description":"Says hi","enabledByDefault":true}
  ],
  "verified": false
}
```

- `verified:true` = merged via PR and reviewed.
- `verified:false` = local drop, shown yellow in Web Plugins Tab.
- Enabling/disabling is per-tool in `%AppData%\RevitAiPoc\settings.json` `plugins:{toolName:bool}`; toggle in Web requires no restart, adding/removing DLL requires restart (AppDomain lock).

See `src/RevitAiPoc/Plugins/Core/IRevitToolPlugin.cs` and `Plugins/example.hello/` for a minimal example.
