# Mendix Vibe Coder

A Mendix Studio Pro extension that adds an AI-powered chat interface for "vibe coding" Mendix applications. Type natural language requests, get MDL commands, and auto-apply them to your project.

## Features

- **Chat Interface** - Dockable pane inside Studio Pro with a modern chat UI
- **AI-Powered** - Uses OpenRouter API to connect to any LLM (Claude, GPT-4o, etc.)
- **MDL Generation** - AI generates Mendix Definition Language commands
- **MDL Validation** - Validates MDL syntax before execution via `mxcli check`
- **MDL Confirmation** - Optional review dialog before executing MDL commands
- **Auto-Execute** - Generated MDL commands are executed via mxcli (toggle in settings)
- **Auto-Sync** - Automatically triggers F4 refresh so changes appear in Studio Pro
- **Project Context** - Auto-fetches project structure (modules, entities, pages) for AI context
- **IModel Integration** - Reads project context directly from Studio Pro's in-memory model
- **MCP Live Editing** - Routes MDL writes through Studio Pro's MCP server for instant changes
- **Chat History Trimming** - Automatically trims old messages to stay within token limits
- **Settings Page** - Configure API key, model, mxcli path, MCP, and behavior

## Prerequisites

1. **Mendix Studio Pro 11.11.0+** with extension development enabled
2. **.NET 8.0 SDK** (for building)
3. **[mxcli](https://github.com/mendixlabs/mxcli)** installed and in PATH (or configure path in settings)
4. **OpenRouter API key** from [openrouter.ai](https://openrouter.ai)

## Enable Extension Development

Add `--enable-extension-development` to your Studio Pro shortcut:

```
"C:\Program Files\Mendix\11.11.0\modeler\studiopro.exe" --enable-extension-development
```

## Build

```bash
cd MendixVibeCoder
dotnet build
```

## Install

1. Build the project
2. Copy the entire `bin/Debug/net8.0/` output to your Mendix app directory:
   ```
   <your-app>/extensions/MendixVibeCoder/
   ```
3. In Studio Pro, press **F4** (Synchronize App Directory)
4. The extension appears under **Extensions > Open Vibe Coder**

## Usage

1. Open a Mendix project in Studio Pro
2. Open the Vibe Coder pane (Extensions menu or View menu)
3. Click the gear icon and configure:
   - Your OpenRouter API key
   - Model ID (e.g., `anthropic/claude-sonnet-4-20250514`)
   - mxcli path
4. Type a request in the chat:
   - "Create a Customer entity with Name, Email and IsActive"
   - "Add a microflow to validate email"
   - "Create an edit page for Customer"
5. AI generates MDL commands and executes them
6. Changes appear in Studio Pro automatically (auto-sync or MCP live editing)

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Auto-execute MDL | On | Execute MDL immediately; off shows review dialog |
| Auto-sync | On | Trigger F4 after MDL execution |
| Auto-fetch context | On | Include project structure in AI prompts |
| Sync delay | 800ms | Delay before F4 press (300-5000ms) |
| Max history tokens | 120,000 | Token limit for chat history |
| Use MCP | On | Route MDL writes through MCP server |
| MCP port | 7782 | Studio Pro MCP server port |
| MCP dial address | 127.0.0.1 | TCP address for MCP connection |

## MCP Live Editing

When MCP is enabled and Studio Pro is running, the extension connects to Studio Pro's built-in MCP server for instant model changes without requiring F4 sync.

To enable MCP in Studio Pro:
1. Open Preferences > AI > MCP Server
2. Enable "Start MCP Server"
3. Note the port (default: 7782)
4. Configure in extension settings

## Project Structure

```
MendixVibeCoder/
├── manifest.json                    # Extension manifest
├── MendixVibeCoder.csproj           # .NET 8.0 project
├── VibeCoderMenuExtension.cs        # Menu item
├── VibeCoderDockablePane.cs         # Dockable pane
├── VibeCoderWebViewViewModel.cs     # WebView + message handling
├── OpenRouterClient.cs              # OpenRouter API client (streaming)
├── MxcliRunner.cs                   # mxcli subprocess runner (direct + MCP)
├── SettingsManager.cs               # Settings persistence
├── ProjectSyncHelper.cs             # Auto F4 sync (Windows)
└── wwwroot/
    ├── index.html                   # Chat + settings + confirmation UI
    ├── app.js                       # Chat logic + message handling
    └── styles.css                   # VS Code theme styling
```

## License

MIT
