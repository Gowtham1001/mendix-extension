using System.Text;
using System.Text.Json;
using Mendix.StudioPro.ExtensionsAPI.Model;
using Mendix.StudioPro.ExtensionsAPI.UI.DockablePane;
using Mendix.StudioPro.ExtensionsAPI.UI.WebView;

namespace MendixVibeCoder;

public class VibeCoderWebViewViewModel : WebViewDockablePaneViewModel
{
    private readonly Uri _baseUri;
    private readonly Func<IModel?> _getCurrentApp;
    private readonly SettingsManager _settingsManager;
    private readonly OpenRouterClient _openRouterClient;
    private readonly MxcliRunner _mxcliRunner;
    private readonly List<OpenRouterMessage> _chatHistory = new();
    private string? _projectContext;
    private CancellationTokenSource? _streamCts;
    private IWebView? _webView;

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
        var extensionDir = AppDomain.CurrentDomain.BaseDirectory;
        webView.Address = new Uri("file:///" + Path.Combine(extensionDir, "index.html").Replace('\\', '/'));

        webView.MessageReceived += async (_, args) =>
        {
            try
            {
                var doc = JsonDocument.Parse(args.Message);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString() ?? "";

                switch (type)
                {
                    case "sendMessage":
                        await HandleSendMessage(root);
                        break;
                    case "getSettings":
                        HandleGetSettings();
                        break;
                    case "saveSettings":
                        HandleSaveSettings(root);
                        break;
                    case "testMxcli":
                        await HandleTestMxcli();
                        break;
                    case "testOpenRouter":
                        await HandleTestOpenRouter();
                        break;
                    case "fetchContext":
                        await HandleFetchContext();
                        break;
                    case "executeMdl":
                        await HandleExecuteMdl(root);
                        break;
                    case "detectProject":
                        HandleDetectProject();
                        break;
                    case "cancelStream":
                        _streamCts?.Cancel();
                        break;
                    case "confirmMdlExecution":
                        await HandleConfirmMdlExecution(root);
                        break;
                    case "checkMcp":
                        await HandleCheckMcp();
                        break;
                }
            }
            catch (Exception ex)
            {
                SendToWeb(new { type = "error", message = ex.Message });
            }
        };
    }

    private async Task HandleSendMessage(JsonElement root)
    {
        var userMessage = root.GetProperty("message").GetString() ?? "";
        _chatHistory.Add(new OpenRouterMessage { Role = "user", Content = userMessage });

        TrimChatHistory();

        _streamCts?.Cancel();
        _streamCts = new CancellationTokenSource();
        var ct = _streamCts.Token;

        var assistantContent = new StringBuilder();
        var mdlCommands = new List<string>();

        try
        {
            await foreach (var chunk in _openRouterClient.StreamChatAsync(_chatHistory, _projectContext, ct))
            {
                if (chunk.StartsWith("[ERROR]"))
                {
                    SendToWeb(new { type = "error", message = chunk });
                    return;
                }

                assistantContent.Append(chunk);
                SendToWeb(new { type = "aiChunk", content = chunk });
            }

            var fullResponse = assistantContent.ToString();
            _chatHistory.Add(new OpenRouterMessage { Role = "assistant", Content = fullResponse });

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
        }
        catch (Exception ex)
        {
            SendToWeb(new { type = "error", message = $"AI request failed: {ex.Message}" });
        }
    }

    private async Task HandleConfirmMdlExecution(JsonElement root)
    {
        var approved = root.GetProperty("approved").GetBoolean();
        if (!approved) return;

        var commandsArray = root.GetProperty("commands");
        var commands = new List<string>();
        foreach (var cmd in commandsArray.EnumerateArray())
        {
            var cmdStr = cmd.GetString();
            if (!string.IsNullOrWhiteSpace(cmdStr))
                commands.Add(cmdStr);
        }

        if (commands.Count > 0)
        {
            _streamCts ??= new CancellationTokenSource();
            await ExecuteMdlCommands(commands, _streamCts.Token);
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
                useMcp = s.UseMcp,
                mcpPort = s.McpPort,
                mcpDialAddress = s.McpDialAddress
            }
        });
    }

    private void HandleSaveSettings(JsonElement root)
    {
        var settings = root.GetProperty("settings");
        _settingsManager.Update(s =>
        {
            if (settings.TryGetProperty("openRouterApiKey", out var v))
                s.OpenRouterApiKey = v.GetString() ?? "";
            if (settings.TryGetProperty("modelId", out v))
                s.ModelId = v.GetString() ?? "anthropic/claude-sonnet-4-20250514";
            if (settings.TryGetProperty("mxcliPath", out v))
                s.MxcliPath = v.GetString() ?? "mxcli";
            if (settings.TryGetProperty("autoSync", out v))
                s.AutoSync = v.GetBoolean();
            if (settings.TryGetProperty("autoFetchContext", out v))
                s.AutoFetchContext = v.GetBoolean();
            if (settings.TryGetProperty("syncDelayMs", out v))
                s.SyncDelayMs = v.GetInt32();
            if (settings.TryGetProperty("autoExecuteMdl", out v))
                s.AutoExecuteMdl = v.GetBoolean();
            if (settings.TryGetProperty("maxHistoryTokens", out v))
                s.MaxHistoryTokens = v.GetInt32();
            if (settings.TryGetProperty("useMcp", out v))
                s.UseMcp = v.GetBoolean();
            if (settings.TryGetProperty("mcpPort", out v))
                s.McpPort = v.GetInt32();
            if (settings.TryGetProperty("mcpDialAddress", out v))
                s.McpDialAddress = v.GetString() ?? "127.0.0.1";
        });

        _mxcliRunner.ResetMcpCache();
        SendToWeb(new { type = "settingsSaved", success = true });
    }

    private async Task HandleTestMxcli()
    {
        var ok = await _mxcliRunner.TestConnectionAsync();
        SendToWeb(new
        {
            type = "mxcliTestResult",
            success = ok,
            message = ok ? "mxcli found and working" : "mxcli not found. Check the path in Settings."
        });
    }

    private async Task HandleTestOpenRouter()
    {
        var result = await _openRouterClient.TestConnectionAsync();
        SendToWeb(new
        {
            type = "openRouterTestResult",
            success = !result.Contains("failed"),
            message = result
        });
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

    private async Task HandleExecuteMdl(JsonElement root)
    {
        var command = root.GetProperty("command").GetString() ?? "";
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
        var removedCount = 0;

        while (_chatHistory.Count > 2)
        {
            var totalTokens = _chatHistory.Sum(m => OpenRouterClient.EstimateTokens(m.Content));
            if (totalTokens <= maxTokens) break;
            _chatHistory.RemoveAt(0);
            removedCount++;
        }

        if (removedCount > 0)
        {
            var droppedMessages = _chatHistory.Take(removedCount).ToList();
            var summaryParts = new List<string>();
            foreach (var msg in droppedMessages)
            {
                var preview = msg.Content.Length > 120 ? msg.Content[..120] + "..." : msg.Content;
                summaryParts.Add($"[{msg.Role}]: {preview}");
            }

            var summary = $"[Earlier conversation summary — {removedCount} message(s) trimmed due to token limit]\n" +
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
        _webView?.PostMessage(json);
    }
}
