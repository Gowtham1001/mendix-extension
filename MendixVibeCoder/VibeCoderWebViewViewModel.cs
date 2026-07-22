using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mendix.StudioPro.ExtensionsAPI.Model;
using Mendix.StudioPro.ExtensionsAPI.UI.DockablePane;
using Mendix.StudioPro.ExtensionsAPI.UI.WebView;

namespace MendixVibeCoder;

public class VibeCoderWebViewViewModel : WebViewDockablePaneViewModel
{
    private readonly Uri? _baseUri;
    private readonly Func<IModel?> _getCurrentApp;
    private IWebView? _webView;
    private readonly SettingsManager _settingsManager;
    private readonly OpenRouterClient _openRouterClient;
    private readonly MxcliRunner _mxcliRunner;
    private readonly List<OpenRouterMessage> _chatHistory = new();
    private readonly object _chatHistoryLock = new();
    private readonly object _streamLock = new();
    private string? _projectContext;
    private CancellationTokenSource? _streamCts;
    private SynchronizationContext? _uiContext;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public VibeCoderWebViewViewModel(Uri baseUri, Func<IModel?> getCurrentApp)
    {
        _baseUri = baseUri;
        _getCurrentApp = getCurrentApp;
        _settingsManager = new SettingsManager();
        _openRouterClient = new OpenRouterClient(_settingsManager);
        _mxcliRunner = new MxcliRunner(_settingsManager);
    }

    public override void InitWebView(IWebView webView)
    {
        _webView = webView;
        _uiContext = SynchronizationContext.Current;

        if (_baseUri != null)
        {
            webView.Address = new Uri(_baseUri, "index");
        }

        webView.MessageReceived += (_, args) =>
        {
            // Use Task.Run to safely handle async operations from the async void event handler.
            // Exceptions thrown after the first await in async void propagate to the
            // SynchronizationContext and can crash the host. Wrapping in Task.Run with a
            // top-level catch ensures all exceptions are caught and sent to the UI.
            _ = Task.Run(async () =>
            {
                try
                {
                    var message = args.Message;

                    switch (message)
                    {
                        case "sendMessage":
                            await HandleSendMessage(args.Data);
                            break;
                        case "getSettings":
                            HandleGetSettings();
                            break;
                        case "saveSettings":
                            HandleSaveSettings(args.Data);
                            break;
                        case "testMxcli":
                            await HandleTestMxcli(args.Data);
                            break;
                        case "testOpenRouter":
                            await HandleTestOpenRouter(args.Data);
                            break;
                        case "fetchContext":
                            await HandleFetchContext();
                            break;
                        case "executeMdl":
                            await HandleExecuteMdl(args.Data);
                            break;
                        case "detectProject":
                            HandleDetectProject();
                            break;
                        case "cancelStream":
                            lock (_streamLock)
                            {
                                _streamCts?.Cancel();
                            }
                            break;
                        case "confirmMdlExecution":
                            await HandleConfirmMdlExecution(args.Data);
                            break;
                        case "checkMcp":
                            await HandleCheckMcp();
                            break;
                    }
                }
                catch (OperationCanceledException)
                {
                    // Silently ignore cancellations — the user triggered a cancel.
                }
                catch (Exception ex)
                {
                    SendToWeb(new { type = "error", message = $"AI request failed: {ex.Message}" });
                }
            });
        };
    }

    public void UpdateBaseUri(Uri baseUri)
    {
        if (_webView != null && baseUri != null)
        {
            _webView.Address = new Uri(baseUri, "index");
        }
    }

    private async Task HandleSendMessage(JsonObject? data)
    {
        var userMessage = data?["message"]?.GetValue<string>() ?? "";
        var aiStartSent = false;

        lock (_chatHistoryLock)
        {
            _chatHistory.Add(new OpenRouterMessage { Role = "user", Content = userMessage });
            TrimChatHistory();
        }

        CancellationToken ct;
        lock (_streamLock)
        {
            _streamCts?.Cancel();
            _streamCts?.Dispose();
            _streamCts = new CancellationTokenSource();
            ct = _streamCts.Token;
        }

        var assistantContent = new StringBuilder();
        var mdlCommands = new List<string>();

        try
        {
            SendToWeb(new { type = "aiStart" });
            aiStartSent = true;

            await foreach (var chunk in _openRouterClient.StreamChatAsync(_chatHistory, _projectContext, ct))
            {
                if (chunk.StartsWith("[ERROR]"))
                {
                    var errorMsg = chunk.Length > 8 ? chunk[8..] : "Unknown error";
                    SendToWeb(new { type = "error", message = errorMsg });
                    SendToWeb(new { type = "aiDone", content = "", mdlCommandsFound = 0, mdlCommands = Array.Empty<string>() });
                    return;
                }

                assistantContent.Append(chunk);
                SendToWeb(new { type = "aiChunk", content = chunk });
            }

            var fullResponse = assistantContent.ToString();

            if (string.IsNullOrWhiteSpace(fullResponse))
            {
                SendToWeb(new { type = "error", message = "AI returned an empty response. Check your API key and model settings." });
                SendToWeb(new { type = "aiDone", content = "", mdlCommandsFound = 0, mdlCommands = Array.Empty<string>() });
                return;
            }

            lock (_chatHistoryLock)
            {
                _chatHistory.Add(new OpenRouterMessage { Role = "assistant", Content = fullResponse });
            }

            mdlCommands = ExtractMdlCommands(fullResponse);

            SendToWeb(new
            {
                type = "aiDone",
                content = fullResponse,
                mdlCommandsFound = mdlCommands.Count,
                mdlCommands = mdlCommands.ToArray()
            });

            if (mdlCommands.Count > 0)
            {
                var settings = _settingsManager.Get();
                if (!settings.AutoExecuteMdl)
                {
                    SendToWeb(new
                    {
                        type = "mdlConfirmationRequired",
                        commands = mdlCommands.ToArray()
                    });
                }
                else
                {
                    await ExecuteMdlCommands(mdlCommands, ct);
                }
            }
        }
        catch (OperationCanceledException)
        {
            SendToWeb(new { type = "streamCancelled" });
            SendToWeb(new { type = "aiDone", content = "", mdlCommandsFound = 0, mdlCommands = Array.Empty<string>() });
        }
        catch (Exception ex)
        {
            // Do NOT remove the user message from history on error — keep it for context
            // and so the user can see what they asked. Just send the error to the UI.

            if (aiStartSent)
            {
                SendToWeb(new { type = "error", message = $"AI request failed: {ex.Message}" });
                SendToWeb(new { type = "aiDone", content = "", mdlCommandsFound = 0, mdlCommands = Array.Empty<string>() });
            }
            else
            {
                SendToWeb(new { type = "error", message = $"AI request failed: {ex.Message}" });
            }
        }
    }

    private async Task HandleConfirmMdlExecution(JsonObject? data)
    {
        var approved = data?["approved"]?.GetValue<bool>() ?? false;
        if (!approved) return;

        var commandsArray = data?["commands"]?.AsArray();
        var commands = new List<string>();
        if (commandsArray != null)
        {
            foreach (var cmd in commandsArray)
            {
                var cmdStr = cmd?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(cmdStr))
                    commands.Add(cmdStr);
            }
        }

        if (commands.Count > 0)
        {
            CancellationToken ct;
            lock (_streamLock)
            {
                _streamCts?.Cancel();
                _streamCts?.Dispose();
                _streamCts = new CancellationTokenSource();
                ct = _streamCts.Token;
            }
            await ExecuteMdlCommands(commands, ct);
        }
    }

    private async Task ExecuteMdlCommands(List<string> commands, CancellationToken ct)
    {
        var settings = _settingsManager.Get();

        foreach (var cmd in commands)
        {
            SendToWeb(new { type = "mdlExecuting", command = cmd });

            var validation = await _mxcliRunner.ValidateMdlAsync(cmd, ct);
            if (!validation.Success)
            {
                SendToWeb(new
                {
                    type = "mdlResult",
                    command = cmd,
                    success = false,
                    output = validation.Output,
                    error = $"Validation failed: {validation.Error}"
                });
                break;
            }

            var result = await _mxcliRunner.RunMdlAsync(cmd, ct);
            SendToWeb(new
            {
                type = "mdlResult",
                command = cmd,
                success = result.Success,
                output = result.Output,
                error = result.Error
            });

            if (result.Success && settings.AutoSync)
            {
                await ProjectSyncHelper.TriggerSyncAsync(settings.SyncDelayMs);
            }

            if (!result.Success) break;
        }
    }

    private void HandleGetSettings()
    {
        var s = _settingsManager.Get();
        SendToWeb(new
        {
            type = "settingsLoaded",
            settings = new
            {
                openRouterApiKey = s.OpenRouterApiKey,
                modelId = s.ModelId,
                mxcliPath = s.MxcliPath,
                autoSync = s.AutoSync,
                autoFetchContext = s.AutoFetchContext,
                syncDelayMs = s.SyncDelayMs,
                autoExecuteMdl = s.AutoExecuteMdl,
                maxHistoryTokens = s.MaxHistoryTokens,
                maxOutputTokens = s.MaxOutputTokens,
                useMcp = s.UseMcp,
                mcpPort = s.McpPort,
                mcpDialAddress = s.McpDialAddress
            }
        });
    }

    private void HandleSaveSettings(JsonObject? data)
    {
        var settings = data?["settings"]?.AsObject();
        if (settings == null) return;

        _settingsManager.Update(s =>
        {
            if (settings["openRouterApiKey"] != null)
                s.OpenRouterApiKey = settings["openRouterApiKey"]!.GetValue<string>() ?? "";
            if (settings["modelId"] != null)
                s.ModelId = settings["modelId"]!.GetValue<string>() ?? "nvidia/nemotron-nano-9b-v2:free";
            if (settings["mxcliPath"] != null)
                s.MxcliPath = settings["mxcliPath"]!.GetValue<string>() ?? "mxcli";
            if (settings["autoSync"] != null)
                s.AutoSync = settings["autoSync"]!.GetValue<bool>();
            if (settings["autoFetchContext"] != null)
                s.AutoFetchContext = settings["autoFetchContext"]!.GetValue<bool>();
            if (settings["syncDelayMs"] != null)
                s.SyncDelayMs = settings["syncDelayMs"]!.GetValue<int>();
            if (settings["autoExecuteMdl"] != null)
                s.AutoExecuteMdl = settings["autoExecuteMdl"]!.GetValue<bool>();
            if (settings["maxHistoryTokens"] != null)
                s.MaxHistoryTokens = settings["maxHistoryTokens"]!.GetValue<int>();
            if (settings["maxOutputTokens"] != null)
                s.MaxOutputTokens = settings["maxOutputTokens"]!.GetValue<int>();
            if (settings["useMcp"] != null)
                s.UseMcp = settings["useMcp"]!.GetValue<bool>();
            if (settings["mcpPort"] != null)
                s.McpPort = settings["mcpPort"]!.GetValue<int>();
            if (settings["mcpDialAddress"] != null)
                s.McpDialAddress = settings["mcpDialAddress"]!.GetValue<string>() ?? "127.0.0.1";
        });

        _mxcliRunner.ResetMcpCache();
        SendToWeb(new { type = "settingsSaved", success = true });
    }

    private async Task HandleTestMxcli(JsonObject? data)
    {
        try
        {
            var formPath = data?["mxcliPath"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(formPath))
            {
                _settingsManager.Update(s => s.MxcliPath = formPath);
            }

            var ok = await _mxcliRunner.TestConnectionAsync();
            SendToWeb(new
            {
                type = "mxcliTestResult",
                success = ok,
                message = ok ? "mxcli found and working" : "mxcli not found. Check the path in Settings."
            });
        }
        catch (Exception ex)
        {
            SendToWeb(new
            {
                type = "mxcliTestResult",
                success = false,
                message = $"Test failed: {ex.Message}"
            });
        }
    }

    private async Task HandleTestOpenRouter(JsonObject? data)
    {
        try
        {
            var formApiKey = data?["apiKey"]?.GetValue<string>()?.Trim();
            var formModelId = data?["modelId"]?.GetValue<string>()?.Trim();
            _settingsManager.Update(s =>
            {
                if (!string.IsNullOrWhiteSpace(formApiKey))
                    s.OpenRouterApiKey = formApiKey;
                if (!string.IsNullOrWhiteSpace(formModelId))
                    s.ModelId = formModelId;
            });

            var result = await _openRouterClient.TestConnectionAsync();
            SendToWeb(new
            {
                type = "openRouterTestResult",
                success = result.StartsWith("Connection successful"),
                message = result
            });
        }
        catch (Exception ex)
        {
            SendToWeb(new
            {
                type = "openRouterTestResult",
                success = false,
                message = $"Test failed: {ex.Message}"
            });
        }
    }

    private async Task HandleCheckMcp()
    {
        var available = await _mxcliRunner.IsMcpAvailableAsync();
        SendToWeb(new
        {
            type = "mcpStatus",
            available,
            message = available
                ? "MCP server detected. Live editing enabled."
                : "MCP server not available. Using direct mode with F4 sync."
        });
    }

    private async Task HandleFetchContext()
    {
        var currentApp = _getCurrentApp();
        if (currentApp != null)
        {
            var context = BuildContextFromModel(currentApp);
            if (!string.IsNullOrEmpty(context))
            {
                _projectContext = context;
                SendToWeb(new
                {
                    type = "contextLoaded",
                    context
                });
                return;
            }
        }

        if (_mxcliRunner.GetProjectPath() == null)
        {
            SendToWeb(new { type = "error", message = "No project detected. Open a Mendix project in Studio Pro." });
            return;
        }

        SendToWeb(new { type = "contextLoading" });

        var result = await _mxcliRunner.GetProjectContextAsync();
        if (result.Success)
        {
            _projectContext = result.Output;
            SendToWeb(new
            {
                type = "contextLoaded",
                context = result.Output
            });
        }
        else
        {
            SendToWeb(new { type = "error", message = $"Failed to fetch context: {result.Error}" });
        }
    }

    private static string? BuildContextFromModel(IModel currentApp)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== PROJECT MODULES ===");

            var project = currentApp.Root;
            var modules = project.GetModules();
            foreach (var module in modules)
            {
                sb.AppendLine($"\n## Module: {module.Name}");

                var domainModel = module.DomainModel;
                var entities = domainModel.GetEntities();
                if (entities.Any())
                {
                    sb.AppendLine($"\n### Entities in {module.Name}");
                    foreach (var entity in entities)
                    {
                        var attrs = entity.GetAttributes();
                        var attrList = string.Join(", ", attrs.Select(a => $"{a.Name}: {a.Type}"));
                        sb.AppendLine($"  {entity.Name} ({attrList})");

                        var associations = entity.GetAssociations(Mendix.StudioPro.ExtensionsAPI.Model.DomainModels.AssociationDirection.Both);
                        foreach (var ea in associations)
                        {
                            var assoc = ea.Association;
                            var type = assoc.Type;
                            var owner = assoc.Owner;
                            sb.AppendLine($"    association: {assoc.Name} ({type}, owner={owner})");
                        }
                    }
                }

                var documents = module.GetDocuments();
                var microflows = documents.OfType<Mendix.StudioPro.ExtensionsAPI.Model.Microflows.IMicroflow>();
                if (microflows.Any())
                {
                    sb.AppendLine($"\n### Microflows in {module.Name}");
                    foreach (var mf in microflows)
                    {
                        sb.AppendLine($"  {mf.Name}");
                    }
                }

                var pages = documents.OfType<Mendix.StudioPro.ExtensionsAPI.Model.Pages.IPage>();
                if (pages.Any())
                {
                    sb.AppendLine($"\n### Pages in {module.Name}");
                    foreach (var page in pages)
                    {
                        sb.AppendLine($"  {page.Name}");
                    }
                }
            }

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private async Task HandleExecuteMdl(JsonObject? data)
    {
        var command = data?["command"]?.GetValue<string>() ?? "";
        SendToWeb(new { type = "mdlExecuting", command });

        var result = await _mxcliRunner.RunMdlAsync(command);
        SendToWeb(new
        {
            type = "mdlResult",
            command,
            success = result.Success,
            output = result.Output,
            error = result.Error
        });

        if (result.Success && _settingsManager.Get().AutoSync)
        {
            await ProjectSyncHelper.TriggerSyncAsync(_settingsManager.Get().SyncDelayMs);
            SendToWeb(new { type = "syncTriggered" });
        }
    }

    private void HandleDetectProject()
    {
        var currentApp = _getCurrentApp();
        if (currentApp != null)
        {
            try
            {
                var firstModule = currentApp.Root.GetModules().FirstOrDefault();
                if (firstModule != null)
                {
                    SendToWeb(new
                    {
                        type = "projectDetected",
                        path = (string?)null,
                        name = firstModule.Name
                    });
                    return;
                }
            }
            catch
            {
            }
        }

        var sp = FindStudioProProject();
        if (sp != null)
        {
            _mxcliRunner.SetProjectPath(sp);
            SendToWeb(new
            {
                type = "projectDetected",
                path = sp,
                name = Path.GetFileNameWithoutExtension(sp)
            });
        }
        else
        {
            SendToWeb(new { type = "projectDetected", path = (string?)null, name = (string?)null });
        }
    }

    private void TrimChatHistory()
    {
        var settings = _settingsManager.Get();
        var maxTokens = settings.MaxHistoryTokens;
        var removedMessages = new List<OpenRouterMessage>();

        while (_chatHistory.Count > 2)
        {
            var totalTokens = _chatHistory.Sum(m => OpenRouterClient.EstimateTokens(m.Content));
            if (totalTokens <= maxTokens) break;

            var removeIndex = _chatHistory.FindIndex(m => m.Role != "system");
            if (removeIndex < 0 || removeIndex >= _chatHistory.Count - 2) break;

            removedMessages.Add(_chatHistory[removeIndex]);
            _chatHistory.RemoveAt(removeIndex);
        }

        if (removedMessages.Count > 0)
        {
            var summaryParts = new List<string>();
            foreach (var msg in removedMessages)
            {
                var preview = msg.Content.Length > 120 ? msg.Content[..120] + "..." : msg.Content;
                summaryParts.Add($"[{msg.Role}]: {preview}");
            }

            var summary = $"[Earlier conversation summary — {removedMessages.Count} message(s) trimmed due to token limit]\n" +
                          string.Join("\n", summaryParts);

            _chatHistory.Insert(0, new OpenRouterMessage
            {
                Role = "system",
                Content = OpenRouterClient.SUMMARY_PROMPT + "\n\n" + summary
            });
        }
    }

    private static List<string> ExtractMdlCommands(string response)
    {
        var commands = new List<string>();
        var parts = response.Split("```mdl", StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            var endIdx = part.IndexOf("```");
            var block = endIdx >= 0 ? part[..endIdx] : part;
            var cmd = block.Trim();

            if (string.IsNullOrWhiteSpace(cmd)) continue;

            var statements = ExtractStatements(cmd);
            commands.AddRange(statements);
        }

        return commands;
    }

    private static List<string> ExtractStatements(string block)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var inBlock = false;

        foreach (var ch in block)
        {
            if (ch == '(' && !inBlock)
            {
                depth++;
                current.Append(ch);
            }
            else if (ch == ')' && inBlock)
            {
                depth--;
                current.Append(ch);
                if (depth == 0) inBlock = false;
            }
            else if (ch == ';' && depth == 0 && !inBlock)
            {
                var stmt = current.ToString().Trim();
                stmt = string.Join("\n", stmt.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("--")));

                if (!string.IsNullOrWhiteSpace(stmt))
                    statements.Add(stmt);

                current.Clear();
            }
            else
            {
                current.Append(ch);

                if (depth == 0 && current.Length > 4)
                {
                    var tail = current.ToString().ToUpperInvariant();
                    if (tail.EndsWith("BEGIN") || tail.EndsWith("{"))
                    {
                        inBlock = true;
                        depth = 1;
                    }
                }
            }
        }

        var remaining = current.ToString().Trim();
        remaining = string.Join("\n", remaining.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("--")));

        if (!string.IsNullOrWhiteSpace(remaining) && remaining.Contains(' '))
            statements.Add(remaining);

        return statements;
    }

    private static string? FindStudioProProject()
    {
        try
        {
            var documentsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var mendixDir = Path.Combine(documentsPath, "Mendix");

            if (Directory.Exists(mendixDir))
            {
                var mprFiles = Directory.GetFiles(mendixDir, "*.mpr", SearchOption.AllDirectories);
                if (mprFiles.Length > 0)
                    return mprFiles.OrderByDescending(File.GetLastWriteTime).First();
            }

            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var desktopDir = Path.Combine(homeDir, "Desktop");

            foreach (var dir in new[] { homeDir, desktopDir })
            {
                if (!Directory.Exists(dir)) continue;
                try
                {
                    var files = Directory.GetFiles(dir, "*.mpr", SearchOption.TopDirectoryOnly);
                    if (files.Length > 0)
                        return files.OrderByDescending(File.GetLastWriteTime).First();
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private void SendToWeb(object data)
    {
        var json = JsonSerializer.Serialize(data, JsonOptions);
        if (_uiContext != null)
        {
            _uiContext.Post(_ => _webView?.PostMessage(json), null);
        }
        else
        {
            _webView?.PostMessage(json);
        }
    }
}
