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

    [JsonPropertyName("error")]
    public OpenRouterError? Error { get; set; }
}

public class OpenRouterError
{
    [JsonPropertyName("code")]
    public object? Code { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
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

    private static readonly HttpClient HttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false
    })
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private static readonly string SYSTEM_PROMPT = """
        You are MendixVibeCoder, an AI assistant that helps developers build Mendix applications using MDL (Mendix Definition Language). You generate MDL commands that are executed inside Mendix Studio Pro via the mxcli tool.

        ## Your Role
        You receive natural language requests from developers and generate MDL commands to create or modify Mendix project elements. You can see the current project structure from the project context below. Use the EXACT module and element names from the context when referencing existing items.

        ## Worked Example

        User: "Create a Customer entity with Name, Email and a Status enum, then make a page to view them"

        You respond:
        First, I'll create the Status enumeration, then the Customer entity, an association, and an overview page.

        ```mdl
        CREATE ENUMERATION MyModule.Status (
          Active 'Active',
          Inactive 'Inactive',
          Archived 'Archived'
        );
        ```

        ```mdl
        CREATE PERSISTENT ENTITY MyModule.Customer (
          Name: String(200) NOT NULL,
          Email: String(255) NOT NULL,
          Status: MyModule.Status
        );
        ```

        ```mdl
        CREATE PAGE MyModule.Customer_Overview
        (
          title: 'Customer Overview',
          layout: Atlas_Core.ContentLayout
        )
        {
          listview lvCustomers (datasource: MyModule.Customer) {
            textbox txtName (label: 'Name', attribute: Name)
            textbox txtEmail (label: 'Email', attribute: Email)
            textbox txtStatus (label: 'Status', attribute: Status)
          }
        }
        ```

        I created:
        - **Status** enum with Active, Inactive, Archived values
        - **Customer** entity with Name, Email, and Status attributes
        - **Customer_Overview** page with a list view showing all customers

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
        Key microflow activities:
        - **CREATE** `$var AS Module.Entity` — instantiate object
        - **CHANGE** `$var (Attr = value, Attr2 = $other)` — set attributes
        - **RETRIEVE** `Module.Entity XPath `[Name = 'test']` INTO $obj` — fetch by constraint
        - **RETRIEVE** `Module.Entity LIST INTO $list` — fetch all
        - **COMMIT** `$var` / **ROLLBACK** `$var`
        - **ASSOCIATE** `$source WITH Module.Assoc $target`
        - **DEASSOCIATE** `$source FROM Module.Assoc $target`
        - **IF** `$var/Attr = value` **THEN** ... **ELSE** ...
        - **LOOP** `$list` **DO** ... **END**
        - **RETURN** `$var` or `empty`
        - **CALL** `Module.MicroflowName($param)` — invoke another microflow
        - **VALIDATE** `$var/Attr` **WITH MESSAGE** `'Error'`
        - **REMOVE** `$var` — delete from database
        - **THROW** `'Error message'`

        ### Nanoflows
        ```mdl
        CREATE NANOFLOW Module.NF_Name
        BEGIN
          ...activity...
        END;
        ```
        Nanoflow activities (client-side):
        - **CallNanoflow** — invoke another nanoflow
        - **OpenPage** `'Module.PageName'`
        - **ClosePage**
        - **ShowMessage** `'text'`
        - **Change** `$currentObject/Attr = value`
        - **Retrieve** from context or by association
        - **If** / **Else** / **End if**
        - **Return** `$value` or `empty`

        ### Pages — Widget Reference
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
        Available widgets:
        - **dataview** `(datasource: $Object)` — single object container
        - **listview** `(datasource: Module.Entity)` — list of objects
        - **templategrid** `(datasource: Module.Entity)` — grid of templates
        - **textbox** `(label: '...', attribute: Attr)` — text input
        - **numberinput** `(label: '...', attribute: Attr)` — number input
        - **datepicker** `(label: '...', attribute: Attr)` — date input
        - **checkbox** `(label: '...', attribute: Attr)` — boolean toggle
        - **dropdown** `(label: '...', attribute: Attr)` — enum dropdown
        - **actionbutton** `(caption: '...', action: save_changes|cancel_changes|call_mf|...)` — button
        - **container** `()` — layout grouping
        - **groupbox** `(caption: '...')` — labeled group
        - **tabcontainer** `()` — tabbed layout with tabpages
        - **tabpage** `(title: '...')` — individual tab
        - **text** `(content: '...')` — static text/HTML
        - **image** `(datasource: $Object/Attribute)` — image display
        - **iframe** `(url: '...')` — embedded web content

        ### Security
        ```mdl
        CREATE MODULE ROLE Module.Admin DESCRIPTION 'Full access';
        GRANT Module.Admin ON Module.EntityName (CREATE, READ *, WRITE *);
        GRANT VIEW ON PAGE Module.PageName TO Module.Admin;
        REVOKE Module.Admin ON Module.EntityName (DELETE *);
        ```

        ## Gotchas
        1. EXTENDS goes BEFORE the opening parenthesis: `ENTITY Module.Child EXTENDS Module.Parent (`
        2. Each ```mdl block should contain ONE logical operation (one entity, one association, one page)
        3. Always use fully qualified names: `Module.EntityName`, not just `EntityName`
        4. Enum values use quotes: `Active 'Active'`
        5. Page widget attributes reference the entity attribute name, not a path
        6. Microflow parameters start with `$`: `$Param: Module.Entity`
        7. XPath constraints use backticks: `RETRIEVE Module.Entity XPath \`[Name = 'test']\` INTO $obj`

        ## Limitations (what MDL cannot do)
        - Cannot create custom widgets or JavaScript actions
        - Cannot modify Nanoflow logic beyond basic activities
        - Cannot set complex XPath constraints with aggregates
        - Cannot create OQL queries
        - Cannot modify Studio Pro theme or styling
        - Cannot create deployment or configuration settings
        - Cannot interact with Marketplace modules directly

        ## Rules
        1. ALWAYS output MDL commands in ```mdl code blocks
        2. Use the EXACT module name from the project context when referencing existing elements
        3. If no project context is provided, use "MyModule" as default module
        4. Use proper Mendix naming conventions (PascalCase for entities/attributes, underscores for pages: `Customer_Overview`)
        5. For complex requests, break them into multiple ```mdl blocks, one per logical operation
        6. ALWAYS terminate MDL statements with semicolons
        7. For multi-line blocks (CREATE MICROFLOW, CREATE PAGE), use BEGIN...END; or {...}
        8. Do NOT mix multiple CREATE statements in one block unless they are semicolon-separated
        9. When creating entities, always include at least a Name or Id attribute
        10. After generating MDL, briefly explain what was created in a summary list
        11. If the request is ambiguous, make reasonable assumptions and state them
        12. If an MDL command fails, analyze the error and suggest a corrected version
        13. When the user references an existing element, use its exact name from the project context
        14. For pages, always include at least one layout container (dataview, listview, or container)
        """;

    internal static readonly string SUMMARY_PROMPT = """
        The above is a summary of our earlier conversation. Continue helping the user with their Mendix project. Use the project context below to reference existing elements accurately.
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
        var apiKey = settings.OpenRouterApiKey?.Trim();

        if (string.IsNullOrWhiteSpace(apiKey))
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
            Stream = true,
            MaxTokens = settings.MaxOutputTokens
        };

        await foreach (var chunk in StreamChatCoreAsync(request, apiKey, ct))
        {
            yield return chunk;
        }
    }

    private async IAsyncEnumerable<string> StreamChatCoreAsync(
        OpenRouterRequest request,
        string apiKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (var attempt = 0; attempt <= MAX_RETRIES; attempt++)
        {
            var result = await SendStreamingRequestAsync(request, apiKey, ct);

            if (result.ErrorMessage != null)
            {
                if (result.ShouldRetry && attempt < MAX_RETRIES)
                {
                    var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                    await Task.Delay(delay, ct);
                    continue;
                }
                yield return $"[ERROR] {result.ErrorMessage}";
                yield break;
            }

            if (result.Reader != null)
            {
                try
                {
                    await foreach (var chunk in ReadStreamChunksAsync(result.Reader, ct))
                    {
                        yield return chunk;
                    }
                }
                finally
                {
                    result.Reader.Dispose();
                    result.Response?.Dispose();
                }
                yield break;
            }

            result.Response?.Dispose();
        }
    }

    private async Task<StreamingRequestResult> SendStreamingRequestAsync(
        OpenRouterRequest request,
        string apiKey,
        CancellationToken ct)
    {
        HttpResponseMessage? response = null;
        StreamReader? reader = null;
        try
        {
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, API_URL)
            {
                Content = JsonContent.Create(request)
            };
            httpRequest.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");

            response = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = response.StatusCode;
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                var errorMsg = ExtractErrorMessage(errorBody, statusCode);

                // Provide actionable guidance for common errors
                if (statusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    errorMsg = $"Bad Request (400): {errorMsg}. " +
                               "This usually means the request is invalid — check your model ID, " +
                               "API key, or that the total context (system prompt + history) " +
                               "does not exceed the model's context window. " +
                               "Try reducing Max History Tokens in Settings.";
                }

                response.Dispose();
                return new StreamingRequestResult
                {
                    ErrorMessage = errorMsg,
                    ShouldRetry = IsRetryable(statusCode)
                };
            }

            var stream = await response.Content.ReadAsStreamAsync(ct);
            reader = new StreamReader(stream);
            return new StreamingRequestResult { Response = response, Reader = reader };
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            reader?.Dispose();
            throw;
        }
        catch (HttpRequestException ex)
        {
            response?.Dispose();
            reader?.Dispose();
            return new StreamingRequestResult { ErrorMessage = $"Connection failed: {ex.Message}" };
        }
        catch (Exception ex)
        {
            response?.Dispose();
            reader?.Dispose();
            return new StreamingRequestResult { ErrorMessage = $"Unexpected error: {ex.Message}" };
        }
    }

    private class StreamingRequestResult
    {
        public HttpResponseMessage? Response { get; set; }
        public StreamReader? Reader { get; set; }
        public string? ErrorMessage { get; set; }
        public bool ShouldRetry { get; set; }
    }

    private static string ExtractErrorMessage(string body, System.Net.HttpStatusCode status)
    {
        try
        {
            var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                if (error.TryGetProperty("message", out var msg))
                {
                    var message = msg.GetString();
                    if (!string.IsNullOrEmpty(message))
                        return message;
                }
            }
        }
        catch { }
        return $"HTTP {(int)status} {status}";
    }

    private static bool IsRetryable(System.Net.HttpStatusCode status) =>
        status is System.Net.HttpStatusCode.TooManyRequests
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.ServiceUnavailable;

    private static async IAsyncEnumerable<string> ReadStreamChunksAsync(
        StreamReader reader,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            OpenRouterChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<OpenRouterChunk>(data);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk?.Error != null)
            {
                var errMsg = chunk.Error.Message ?? chunk.Error.Code?.ToString() ?? "Unknown stream error";
                yield return $"[ERROR] Stream error: {errMsg}";
                yield break;
            }

            var content = chunk?.Choices?.FirstOrDefault()?.Delta?.Content;
            if (!string.IsNullOrEmpty(content))
            {
                yield return content;
            }
        }
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        var settings = _settings.Get();
        var apiKey = settings.OpenRouterApiKey?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
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
