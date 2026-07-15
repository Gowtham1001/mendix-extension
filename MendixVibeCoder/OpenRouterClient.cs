using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MendixVibeCoder;

public class OpenRouterMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "user";

    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

public class OpenRouterRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("messages")]
    public List<OpenRouterMessage> Messages { get; set; } = new();

    [JsonPropertyName("stream")]
    public bool Stream { get; set; } = true;

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; } = 0.1;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; } = 8192;
}

public class OpenRouterChunk
{
    [JsonPropertyName("choices")]
    public List<OpenRouterChoice>? Choices { get; set; }
}

public class OpenRouterChoice
{
    [JsonPropertyName("delta")]
    public OpenRouterDelta? Delta { get; set; }
}

public class OpenRouterDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; set; }
}

public class OpenRouterClient
{
    private const string API_URL = "https://openrouter.ai/api/v1/chat/completions";
    private const int MAX_RETRIES = 3;

    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private static readonly string SYSTEM_PROMPT = """
        You are MendixVibeCoder, an AI assistant that helps developers build Mendix applications using MDL (Mendix Definition Language). You generate MDL commands that are executed inside Mendix Studio Pro via the mxcli tool.

        ## Your Role
        You receive natural language requests from developers and generate MDL commands to create or modify Mendix project elements. You can see the current project structure from context.

        ## MDL Syntax Reference

        ### Entities
        ```mdl
        CREATE PERSISTENT ENTITY Module.EntityName (
          AttributeName: Type(Length) CONSTRAINTS,
          ...
        );
        ```
        - Types: String(200), Integer, Long, Decimal(10,2), Boolean, DateTime, AutoNumber, Enum
        - Constraints: NOT NULL, UNIQUE, DEFAULT value
        - Non-persistent: `CREATE NON-PERSISTENT ENTITY Module.Name (...);`
        - Create or modify: `CREATE OR MODIFY PERSISTENT ENTITY Module.Name (...);`

        ### Entity Generalization
        ```mdl
        CREATE PERSISTENT ENTITY Module.Child EXTENDS Module.Parent (
          ExtraAttr: String(100)
        );
        ```
        **CRITICAL: EXTENDS goes BEFORE the opening parenthesis, not after!**

        ### Alter Entity
        ```mdl
        ALTER ENTITY Module.Name ADD (NewAttr: String(100));
        ALTER ENTITY Module.Name DROP (OldAttr);
        ALTER ENTITY Module.Name MODIFY (Attr: NewType(200));
        ALTER ENTITY Module.Name RENAME OldName TO NewName;
        ALTER ENTITY Module.Name SET DOCUMENTATION 'description text';
        ```

        ### Associations
        ```mdl
        CREATE ASSOCIATION Module.AssocName
          FROM Module.Source TO Module.Target
          TYPE Reference
          OWNER Default
          DELETE_BEHAVIOR Cascade;
        ```
        - TYPE: Reference (single) or ReferenceSet (multi)
        - OWNER: Default or Both
        - DELETE_BEHAVIOR: Cascade, Delete, Warn, RemoveFrom

        ### Enumerations
        ```mdl
        CREATE ENUMERATION Module.Status (
          Active 'Active',
          Inactive 'Inactive'
        );
        ```

        ### Microflows
        ```mdl
        CREATE MICROFLOW Module.MF_Name (
          $Param: Module.Entity
        )
        BEGIN
          CREATE $var AS Module.Entity;
          CHANGE $var (Attribute = value);
          COMMIT $var;
          RETRIEVE Module.Entity LIST INTO $list;
          RETURN $var;
        END;
        ```
        - Activities: CREATE, CHANGE, COMMIT, RETRIEVE, RETURN, CAST, CALL, LOOP, IF/ELSE, SPLIT, EXCEPTION, TRACE, LOG, VALIDATE, REMOVE, ASSOCIATE, DEASSOCIATE, GENERATE, IMPORT_FROM_FILE, EXPORT_TO_FILE, SEND_EMAIL

        ### Nanoflows
        ```mdl
        CREATE NANOFLOW Module.NF_Name
        BEGIN
          ...activity...
        END;
        ```

        ### Pages
        ```mdl
        CREATE PAGE Module.PageName
        (
          params: { $Param: Module.Entity },
          title: 'Page Title',
          layout: Atlas_Core.ContentLayout
        )
        {
          dataview dvName (datasource: $Variable) {
            textbox txtName (label: 'Name', attribute: Name)
            actionbutton btnSave (caption: 'Save', action: save_changes, buttonstyle: primary)
          }
        }
        ```

        ### Security
        ```mdl
        CREATE MODULE ROLE Module.Admin DESCRIPTION 'Full access';
        GRANT Module.Admin ON Module.EntityName (CREATE, READ *, WRITE *);
        GRANT VIEW ON PAGE Module.PageName TO Module.Admin;
        REVOKE Module.Admin ON Module.EntityName (DELETE *);
        ```

        ## Rules
        1. ALWAYS output MDL commands in ```mdl code blocks
        2. Use the EXACT module name from the project context when referencing existing elements
        3. If no project context is provided, use "MyModule" as default module
        4. Use proper Mendix naming conventions (PascalCase for entities/attributes)
        5. For complex requests, break them into multiple ```mdl blocks, one per logical operation
        6. ALWAYS terminate MDL statements with semicolons
        7. For multi-line blocks (CREATE MICROFLOW, CREATE PAGE), use BEGIN...END; or {...}
        8. Do NOT mix multiple CREATE statements in one block unless they are semicolon-separated
        9. When creating entities, always include at least a Name or Id attribute
        10. After generating MDL, briefly explain what was created
        11. If the request is ambiguous, make reasonable assumptions and state them
        """;

    private readonly SettingsManager _settings;

    public OpenRouterClient(SettingsManager settings)
    {
        _settings = settings;
    }

    public static int EstimateTokens(string text)
    {
        return text.Length / 4;
    }

    public async IAsyncEnumerable<string> StreamChatAsync(
        List<OpenRouterMessage> messages,
        string? projectContext = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var settings = _settings.Get();

        if (string.IsNullOrWhiteSpace(settings.OpenRouterApiKey))
        {
            yield return "[ERROR] OpenRouter API key not configured. Open Settings to configure.";
            yield break;
        }

        var systemMessage = new OpenRouterMessage
        {
            Role = "system",
            Content = SYSTEM_PROMPT + (projectContext != null
                ? $"\n\n## Current Project Context\n{projectContext}"
                : "\n\nNo project context available. Use 'MyModule' as default module.")
        };

        var allMessages = new List<OpenRouterMessage> { systemMessage };
        allMessages.AddRange(messages);

        var request = new OpenRouterRequest
        {
            Model = settings.ModelId,
            Messages = allMessages,
            Stream = true
        };

        for (var attempt = 0; attempt <= MAX_RETRIES; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                var httpRequest = new HttpRequestMessage(HttpMethod.Post, API_URL)
                {
                    Content = JsonContent.Create(request, options: new JsonSerializerOptions
                    {
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    })
                };
                httpRequest.Headers.Add("Authorization", $"Bearer {settings.OpenRouterApiKey}");
                httpRequest.Headers.Add("HTTP-Referer", "https://github.com/mendix-vibe-coder");
                httpRequest.Headers.Add("X-Title", "MendixVibeCoder");

                response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ||
                    response.StatusCode == System.Net.HttpStatusCode.BadGateway ||
                    response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                {
                    if (attempt < MAX_RETRIES)
                    {
                        var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                        await Task.Delay(delay, ct);
                        continue;
                    }
                }

                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(stream);

                while (!reader.EndOfStream)
                {
                    ct.ThrowIfCancellationRequested();

                    var line = await reader.ReadLineAsync(ct);
                    if (string.IsNullOrEmpty(line)) continue;
                    if (!line.StartsWith("data: ")) continue;

                    var data = line[6..];
                    if (data == "[DONE]") break;

                    try
                    {
                        var chunk = JsonSerializer.Deserialize<OpenRouterChunk>(data);
                        var content = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
                        if (!string.IsNullOrEmpty(content))
                        {
                            yield return content;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }

                yield break;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception) when (attempt < MAX_RETRIES)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                await Task.Delay(delay, ct);
            }
            finally
            {
                response?.Dispose();
            }
        }
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        var settings = _settings.Get();
        if (string.IsNullOrWhiteSpace(settings.OpenRouterApiKey))
            return "API key not configured";

        try
        {
            var messages = new List<OpenRouterMessage>
            {
                new() { Role = "user", Content = "Say 'ok' and nothing else." }
            };

            var sb = new StringBuilder();
            await foreach (var chunk in StreamChatAsync(messages, null, ct))
            {
                if (chunk.StartsWith("[ERROR]"))
                    return chunk;
                sb.Append(chunk);
            }

            var result = sb.ToString().Trim();
            return string.IsNullOrEmpty(result)
                ? "Connection successful (empty response)"
                : $"Connection successful: {result}";
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }
}
