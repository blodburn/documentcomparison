using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentCompare.Avalonia.Models;

namespace DocumentCompare.Avalonia.Engine;

public sealed class PythonBridgeComparisonEngine : IComparisonEngine
{
    // Encoding.UTF8 can emit a preamble when used by a StreamWriter.  The
    // engine protocol is JSON-lines and must never receive a BOM before `{`.
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private const string EmbeddedEngineResourceName = "DocumentCompare.Embedded.DocumentCompare.Engine.exe";
    private static readonly object EngineExtractionLock = new();

    private readonly string _enginePath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private StreamReader? _stderr;
    private Task<string>? _stderrDrainTask;

    public PythonBridgeComparisonEngine(string? enginePath = null)
    {
        _enginePath = ResolveEnginePath(enginePath);
    }

    private static string ResolveEnginePath(string? enginePath)
    {
        if (!string.IsNullOrWhiteSpace(enginePath))
            return enginePath;

        var environmentPath = Environment.GetEnvironmentVariable("DOCUMENT_COMPARE_ENGINE");
        if (!string.IsNullOrWhiteSpace(environmentPath))
            return environmentPath;

        var embeddedPath = ExtractEmbeddedEngineIfPresent();
        if (!string.IsNullOrWhiteSpace(embeddedPath))
            return embeddedPath;

        // Development/backward-compatible fallback.  Release builds embed the engine,
        // while older/dev layouts may still keep a sidecar next to the application.
        return Path.Combine(AppContext.BaseDirectory, "engine", "DocumentCompare.Engine.exe");
    }

    private static string? ExtractEmbeddedEngineIfPresent()
    {
        var assembly = typeof(PythonBridgeComparisonEngine).Assembly;
        using var hashStream = assembly.GetManifestResourceStream(EmbeddedEngineResourceName);
        if (hashStream is null)
            return null;

        var hash = Convert.ToHexString(SHA256.HashData(hashStream)).ToLowerInvariant();
        var hashPrefix = hash[..16];
        var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(baseDirectory))
            baseDirectory = Path.GetTempPath();

        var runtimeDirectory = Path.Combine(baseDirectory, "DocumentCompare", "runtime");
        var destination = Path.Combine(runtimeDirectory, $"DocumentCompare.Engine.{hashPrefix}.exe");

        lock (EngineExtractionLock)
        {
            if (File.Exists(destination) && new FileInfo(destination).Length > 0)
                return destination;

            Directory.CreateDirectory(runtimeDirectory);
            var temporary = destination + $".{Environment.ProcessId}.tmp";
            try
            {
                using var input = assembly.GetManifestResourceStream(EmbeddedEngineResourceName)
                    ?? throw new InvalidOperationException("내장 비교 엔진 리소스를 다시 열 수 없습니다.");
                using (var output = new FileStream(
                    temporary,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    options: FileOptions.SequentialScan))
                {
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                }

                // Another app instance may have completed the same content-addressed
                // extraction while this process was writing. Never overwrite a possibly
                // running engine executable; accept the winner and discard our temp copy.
                try
                {
                    File.Move(temporary, destination);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    File.Delete(temporary);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch { }
            }
        }

        return destination;
    }

    private void EnsureStarted()
    {
        if (_process is { HasExited: false } && _stdin is not null && _stdout is not null)
            return;
        DisposeProcess();
        if (!File.Exists(_enginePath))
            throw new FileNotFoundException(
                "비교 엔진을 찾을 수 없습니다. 개발 실행이라면 BUILD_ENGINE_SIDECAR.cmd를 먼저 실행하세요.",
                _enginePath);

        var psi = new ProcessStartInfo(_enginePath, "--server")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Utf8NoBom,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom
        };
        // Korean Windows may otherwise start embedded Python with CP949 stdio.
        // The protocol is UTF-8 and documents can contain characters such as U+2022.
        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        _process = Process.Start(psi) ?? throw new InvalidOperationException("비교 엔진을 시작하지 못했습니다.");
        _stdin = _process.StandardInput;
        _stdout = _process.StandardOutput;
        _stderr = _process.StandardError;
        _stderrDrainTask = _stderr.ReadToEndAsync();
    }

    public async Task<JsonDocument> SendAsync(CompareRequest request, CancellationToken cancellationToken = default, IProgress<int>? progress = null)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            EnsureStarted();
            var payload = JsonSerializer.Serialize(request, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
            await _stdin!.WriteLineAsync(payload.AsMemory(), cancellationToken);
            await _stdin.FlushAsync(cancellationToken);

            while (true)
            {
                string? line;
                try
                {
                    line = await _stdout!.ReadLineAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    AbortCurrentOperation();
                    throw;
                }
                if (string.IsNullOrWhiteSpace(line))
                {
                    var stderr = _stderrDrainTask?.IsCompletedSuccessfully == true ? _stderrDrainTask.Result : "";
                    throw new InvalidOperationException($"비교 엔진이 결과를 반환하지 않았습니다. {stderr}");
                }

                // Defensive compatibility: old/custom engine wrappers may prepend
                // U+FEFF even though the current writer is BOM-free.
                line = line.TrimStart('\uFEFF');
                var doc = JsonDocument.Parse(line);

                // Protocol v3 progress frames are emitted before the final response.
                // Consumers that do not supply a progress callback still safely drain them.
                if (doc.RootElement.TryGetProperty("type", out var type) &&
                    string.Equals(type.GetString(), "progress", StringComparison.OrdinalIgnoreCase))
                {
                    if (doc.RootElement.TryGetProperty("value", out var value) && value.TryGetInt32(out var percent))
                        progress?.Report(Math.Clamp(percent, 0, 100));
                    doc.Dispose();
                    continue;
                }

                if (doc.RootElement.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
                {
                    var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
                    doc.Dispose();
                    throw new InvalidOperationException(error ?? "비교 엔진 오류");
                }
                return doc;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task PingAsync(CancellationToken cancellationToken = default)
    {
        using var _ = await SendAsync(new CompareRequest("ping", Array.Empty<string>(), 0, "auto"), cancellationToken);
    }

    public void AbortCurrentOperation()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        DisposeProcess();
    }

    private void DisposeProcess()
    {
        try { _stdin?.Dispose(); } catch { }
        try { _stdout?.Dispose(); } catch { }
        try { _stderr?.Dispose(); } catch { }
        try { _process?.Dispose(); } catch { }
        _stdin = null;
        _stdout = null;
        _stderr = null;
        _process = null;
        _stderrDrainTask = null;
    }

    public ValueTask DisposeAsync()
    {
        AbortCurrentOperation();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
