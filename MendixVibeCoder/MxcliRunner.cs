using System.Diagnostics;
using System.Net.Sockets;
using System.Text;

namespace MendixVibeCoder;

public class MxcliResult
{
    public bool Success { get; set; }
    public string Output { get; set; } = "";
    public string Error { get; set; } = "";
    public int ExitCode { get; set; }
}

public class MxcliRunner
{
    private readonly SettingsManager _settings;
    private string? _projectPath;
    private bool? _mcpAvailable;

    public MxcliRunner(SettingsManager settings)
    {
        _settings = settings;
    }

    public void SetProjectPath(string path) => _projectPath = path;

    public string? GetProjectPath() => _projectPath;

    public async Task<MxcliResult> RunCommandAsync(string command, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_projectPath))
            return new MxcliResult { Error = "No project open. Open a Mendix project in Studio Pro first." };

        var mxcliPath = _settings.Get().MxcliPath;
        var arguments = $"-p \"{_projectPath}\" -c \"{command.Replace("\"", "\\\"")}\"";

        return await RunProcessAsync(mxcliPath, arguments, ct);
    }

    public async Task<MxcliResult> RunMdlAsync(string mdl, CancellationToken ct = default)
    {
        var settings = _settings.Get();

        if (settings.UseMcp && await IsMcpAvailableAsync(ct))
        {
            return await RunMdlViaMcpAsync(mdl, ct);
        }

        return await RunCommandAsync(mdl, ct);
    }

    public async Task<MxcliResult> RunMdlViaMcpAsync(string mdl, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_projectPath))
            return new MxcliResult { Error = "No project open." };

        var settings = _settings.Get();
        var mxcliPath = settings.MxcliPath;
        var arguments = $"-p \"{_projectPath}\" --mcp http://localhost/mcp --mcp-dial {settings.McpDialAddress}:{settings.McpPort} -c \"{mdl.Replace("\"", "\\\"")}\"";

        return await RunProcessAsync(mxcliPath, arguments, ct);
    }

    public async Task<MxcliResult> ValidateMdlAsync(string mdlScript, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_projectPath))
            return new MxcliResult { Success = false, Error = "No project open." };

        var mxcliPath = _settings.Get().MxcliPath;
        var arguments = $"check -p \"{_projectPath}\"";

        var result = await RunProcessWithStdinAsync(mxcliPath, arguments, mdlScript, ct);
        result.Success = result.ExitCode == 0;
        return result;
    }

    public async Task<MxcliResult> DescribeAsync(string type, string name, CancellationToken ct = default)
    {
        return await RunCommandAsync($"describe {type} {name}", ct);
    }

    public async Task<MxcliResult> ShowModulesAsync(CancellationToken ct = default)
    {
        return await RunCommandAsync("show modules", ct);
    }

    public async Task<MxcliResult> ShowEntitiesAsync(string module, CancellationToken ct = default)
    {
        return await RunCommandAsync($"show entities in {module}", ct);
    }

    public async Task<MxcliResult> ShowStructureAsync(CancellationToken ct = default)
    {
        return await RunCommandAsync("show structure", ct);
    }

    public async Task<MxcliResult> SearchAsync(string query, CancellationToken ct = default)
    {
        return await RunCommandAsync($"search \"{query}\"", ct);
    }

    public async Task<MxcliResult> CheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(_projectPath))
            return new MxcliResult { Error = "No project open." };

        var mxcliPath = _settings.Get().MxcliPath;
        var arguments = $"check -p \"{_projectPath}\"";
        return await RunProcessAsync(mxcliPath, arguments, ct);
    }

    public async Task<MxcliResult> GetProjectContextAsync(CancellationToken ct = default)
    {
        var sb = new StringBuilder();

        var modules = await ShowModulesAsync(ct);
        if (!modules.Success)
            return modules;

        sb.AppendLine("=== PROJECT MODULES ===");
        sb.AppendLine(modules.Output);

        var lines = modules.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var moduleName = line.Trim().TrimEnd(';').Trim();
            if (string.IsNullOrWhiteSpace(moduleName)) continue;

            var entities = await ShowEntitiesAsync(moduleName, ct);
            if (entities.Success && !string.IsNullOrWhiteSpace(entities.Output))
            {
                sb.AppendLine($"\n=== ENTITIES IN {moduleName} ===");
                sb.AppendLine(entities.Output);
            }
        }

        return new MxcliResult
        {
            Success = true,
            Output = sb.ToString()
        };
    }

    public async Task<bool> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            var mxcliPath = _settings.Get().MxcliPath;
            var result = await RunProcessAsync(mxcliPath, "--version", ct);
            return result.Success && !string.IsNullOrWhiteSpace(result.Output);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsMcpAvailableAsync(CancellationToken ct = default)
    {
        if (_mcpAvailable.HasValue)
            return _mcpAvailable.Value;

        var settings = _settings.Get();
        if (!settings.UseMcp)
        {
            _mcpAvailable = false;
            return false;
        }

        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(settings.McpDialAddress, settings.McpPort);
            var timeoutTask = Task.Delay(2000, ct);

            var completed = await Task.WhenAny(connectTask, timeoutTask);
            _mcpAvailable = completed == connectTask && client.Connected;
            return _mcpAvailable.Value;
        }
        catch
        {
            _mcpAvailable = false;
            return false;
        }
    }

    public void ResetMcpCache() => _mcpAvailable = null;

    private async Task<MxcliResult> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var result = new MxcliResult();

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _projectPath != null
                    ? Path.GetDirectoryName(_projectPath) ?? ""
                    : Environment.CurrentDirectory
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                result.Error = "Command timed out after 30 seconds";
                return result;
            }

            result.Output = stdout.ToString().Trim();
            result.Error = stderr.ToString().Trim();
            result.ExitCode = process.ExitCode;
            result.Success = process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            result.Error = $"Failed to run mxcli: {ex.Message}";
        }

        return result;
    }

    private async Task<MxcliResult> RunProcessWithStdinAsync(string fileName, string arguments, string stdin, CancellationToken ct)
    {
        var result = new MxcliResult();

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _projectPath != null
                    ? Path.GetDirectoryName(_projectPath) ?? ""
                    : Environment.CurrentDirectory
            };

            var stdout = new StringBuilder();
            var stderr = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.StandardInput.WriteAsync(stdin, ct);
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                result.Error = "Command timed out after 30 seconds";
                return result;
            }

            result.Output = stdout.ToString().Trim();
            result.Error = stderr.ToString().Trim();
            result.ExitCode = process.ExitCode;
            result.Success = process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            result.Error = $"Failed to run mxcli check: {ex.Message}";
        }

        return result;
    }
}
