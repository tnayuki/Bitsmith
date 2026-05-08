using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Xunit;

namespace Bitsmith.Llvm.Tests.Integration;

/// <summary>
/// Locates LLVM 15 tooling (llvm-dis, llvm-bcanalyzer, lli, llc) on PATH.
/// Tests that depend on these binaries should call <see cref="Require"/>
/// to skip cleanly when the tool is missing.
/// </summary>
internal static class LlvmTools
{
    public sealed class ToolResult
    {
        public int ExitCode { get; init; }
        public string StdOut { get; init; } = "";
        public string StdErr { get; init; } = "";
    }

    public static string? Find(string name)
    {
        // Try unversioned and -15 suffixed variants. e.g. llvm-dis, llvm-dis-15.
        foreach (var candidate in new[] { name, name + "-15" })
        {
            var path = Which(candidate);
            if (path != null) return path;
        }
        return null;
    }

    public static void Require(string name)
    {
        Skip.If(Find(name) is null, $"{name} not found on PATH; skipping integration test");
    }

    public static ToolResult Run(string tool, params string[] args)
    {
        var path = Find(tool) ?? throw new InvalidOperationException($"{tool} not found");
        var psi = new ProcessStartInfo
        {
            FileName = path,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return new ToolResult { ExitCode = p.ExitCode, StdOut = stdout, StdErr = stderr };
    }

    private static string? Which(string name)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null) return null;
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';')
            : new[] { "" };
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            foreach (var ext in exts)
            {
                var full = Path.Combine(dir, name + ext);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }
}
