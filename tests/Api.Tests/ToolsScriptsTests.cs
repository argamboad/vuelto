using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Vuelto.Api.Tests;

/// <summary>
/// v4 audit T52 (LB-DEP-8, DEP-13 — R140): the operator scripts under <c>tools/</c> have a contract, and the one
/// that decides whether an APK may be handed out is run here against fakes. Under Windows PowerShell 5.1 a
/// BOM-less UTF-8 script with an em dash in a string fails to parse; without <c>$ErrorActionPreference = 'Stop'</c>
/// a failed step prints an error and the script carries on to exit 0. The static half holds every script to the
/// header; the harness half proves <c>publish-native.ps1</c>'s exit codes with a fake <c>dotnet</c> and a fake
/// <c>apksigner</c>, so "refuses to finish unless the signature is verified" is a behaviour, not a sentence.
/// </summary>
public class ToolsScriptsTests
{
    [Fact]
    public void ToolsScripts_RequirePwsh7_AndFailLoud()
    {
        var root = RepoRoot();
        var scripts = Directory.EnumerateFiles(Path.Combine(root, "tools"), "*.ps1").ToList();
        Assert.True(scripts.Count >= 4, "probe: tools/*.ps1 moved");

        var problems = new List<string>();
        foreach (var script in scripts)
        {
            var name = Path.GetFileName(script);
            var bytes = File.ReadAllBytes(script);
            var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = File.ReadAllText(script);

            // Windows PowerShell 5.1 reads a BOM-less file as ANSI: one non-ASCII character in a string is a parse error.
            if (!hasBom && bytes.Any(b => b > 0x7F)) problems.Add($"{name}: non-ASCII characters without a UTF-8 BOM");
            // The first statement: 5.1 refuses to run the script at all, with a message that names the version.
            if (text.TrimStart('﻿').Split('\n').First(l => l.Trim().Length > 0).Trim() != "#Requires -Version 7.0")
                problems.Add($"{name}: the first line is not '#Requires -Version 7.0'");
            if (!Regex.IsMatch(text, @"^\$ErrorActionPreference\s*=\s*['""]Stop['""]", RegexOptions.Multiline))
                problems.Add($"{name}: no top-level $ErrorActionPreference = 'Stop'");
        }
        Assert.True(problems.Count == 0, string.Join("\n", problems));

        // The docs run them through pwsh; `.\tools\x.ps1` in a doc is whatever shell the reader happens to be in.
        var direct = new[] { "README.md", "CONTRIBUTING.md" }.Select(f => Path.Combine(root, f))
            .Concat(Directory.EnumerateFiles(Path.Combine(root, "docs"), "*.md", SearchOption.AllDirectories))
            .Where(f => !f.Replace('\\', '/').Contains("/docs/audits/"))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (f, line, i)))
            .Where(x => Regex.IsMatch(x.line, @"(?<!pwsh )(?<![\w/\\.-])\.?[\\/]?tools[\\/][\w-]+\.ps1\s+-\w"))
            .Select(x => $"{Path.GetRelativePath(root, x.f)}:{x.i + 1}").ToList();
        Assert.True(direct.Count == 0, "a doc runs a tools script without `pwsh`: " + string.Join(", ", direct));
    }

    [Theory]
    [InlineData("v2-true", 0, 0)]      // verified: the APK is copied out and the script says "send THIS"
    [InlineData("v2-false", 0, 1)]     // apksigner says v1-only: never handed out
    [InlineData("v2-true", 1, 1)]      // the publish itself failed
    public async Task PublishNative_ExitsNonZero_UnlessTheApkIsBuiltAndVerified(string apksigner, int dotnetExit, int expectedExit)
    {
        using var fakes = new Fakes(apksigner, dotnetExit);
        var (exit, output) = await fakes.RunAsync("-ApiBaseUrl", "https://probe.invalid", "-Android", "-Out", fakes.Out);

        Assert.True(exit == expectedExit, $"exit {exit}, expected {expectedExit}:\n{output}");
        var handedOut = Directory.Exists(fakes.Out) && Directory.EnumerateFiles(fakes.Out, "*.apk").Any();
        if (expectedExit == 0)
        {
            Assert.True(handedOut, "the verified APK was not copied out");
            Assert.Contains("Send THIS file", output);
        }
        else
        {
            Assert.DoesNotContain("Send THIS file", output);
        }
    }

    [Fact]
    public async Task PublishNative_WithAStoreKey_ButNoPasswordsInTheEnvironment_RefusesBeforeBuilding()
    {
        using var fakes = new Fakes("v2-true", dotnetExit: 0);
        var keystore = Path.Combine(fakes.Dir, "release.jks");
        await File.WriteAllTextAsync(keystore, "not a real keystore");

        var (exit, output) = await fakes.RunAsync("-ApiBaseUrl", "https://probe.invalid", "-Android", "-Out", fakes.Out, "-KeyStore", keystore, "-KeyAlias", "upload");

        Assert.Equal(1, exit);
        Assert.Contains("ANDROID_SIGNING_STORE_PASS", output);
        Assert.False(File.Exists(Path.Combine(fakes.Dir, "dotnet-was-called")), "the build ran without its signing passwords");
    }

    /// <summary>A scratch directory holding a fake <c>dotnet</c> (first on PATH), a fake Android SDK whose
    /// <c>apksigner</c> prints a canned verdict, and a JDK directory that merely exists.</summary>
    private sealed class Fakes : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "tools-scripts-" + Guid.NewGuid().ToString("N"));
        public string Out => Path.Combine(Dir, "out");
        private readonly string _verdict;
        private readonly int _dotnetExit;

        public Fakes(string apksigner, int dotnetExit)
        {
            _verdict = apksigner == "v2-true"
                ? "Verified using v1 scheme (JAR signing): false|Verified using v2 scheme (APK Signature Scheme v2): true|Verified using v3 scheme (APK Signature Scheme v3): true"
                : "Verified using v1 scheme (JAR signing): true|Verified using v2 scheme (APK Signature Scheme v2): false|Verified using v3 scheme (APK Signature Scheme v3): false";
            _dotnetExit = dotnetExit;

            var bin = Directory.CreateDirectory(Path.Combine(Dir, "bin")).FullName;
            var buildTools = Directory.CreateDirectory(Path.Combine(Dir, "sdk", "build-tools", "99.0.0")).FullName;
            Directory.CreateDirectory(Path.Combine(Dir, "jdk")); // (99.0.0: the script takes the newest build-tools across every SDK it finds, a real one included)

            // The fake dotnet: records that it ran, drops the two APKs a real publish leaves under -o, exits as told.
            File.WriteAllText(Path.Combine(bin, "fake-dotnet.ps1"), """
                $o = $args[[array]::IndexOf($args, '-o') + 1]
                Set-Content (Join-Path $env:FAKES_DIR 'dotnet-was-called') 'yes'
                if ($env:FAKE_DOTNET_EXIT -ne '0') { Write-Error 'publish failed'; exit [int]$env:FAKE_DOTNET_EXIT }
                New-Item -ItemType Directory -Force $o | Out-Null
                Set-Content (Join-Path $o 'com.example.app.apk') 'unsigned'
                Set-Content (Join-Path $o 'com.example.app-Signed.apk') 'signed'
                exit 0
                """);
            File.WriteAllText(Path.Combine(buildTools, "fake-apksigner.ps1"), """
                $env:FAKE_APKSIGNER_VERDICT -split '\|' | ForEach-Object { Write-Output $_ }
                exit 0
                """);
            Shim(Path.Combine(bin, "dotnet"), Path.Combine(bin, "fake-dotnet.ps1"));
            Shim(Path.Combine(buildTools, "apksigner"), Path.Combine(buildTools, "fake-apksigner.ps1"));
        }

        /// <summary>A launcher the OS will execute under the plain command name: <c>name.bat</c>/<c>name.cmd</c> on
        /// Windows, an executable shell script elsewhere.</summary>
        private static void Shim(string command, string script)
        {
            if (OperatingSystem.IsWindows())
            {
                var extension = Path.GetFileName(command) == "apksigner" ? ".bat" : ".cmd";
                File.WriteAllText(command + extension, $"@pwsh -NoProfile -File \"{script}\" %*\r\n");
            }
            else
            {
                File.WriteAllText(command, $"#!/bin/sh\nexec pwsh -NoProfile -File \"{script}\" \"$@\"\n");
                File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        public async Task<(int Exit, string Output)> RunAsync(params string[] args)
        {
            var start = new ProcessStartInfo("pwsh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-NoProfile", "-File", Path.Combine(RepoRoot(), "tools", "publish-native.ps1") }.Concat(args))
                start.ArgumentList.Add(a);
            start.Environment["PATH"] = Path.Combine(Dir, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment["ANDROID_HOME"] = Path.Combine(Dir, "sdk");
            start.Environment["ANDROID_SDK_ROOT"] = "";
            start.Environment["JAVA_HOME"] = Path.Combine(Dir, "jdk");
            start.Environment["FAKES_DIR"] = Dir;
            start.Environment["FAKE_DOTNET_EXIT"] = _dotnetExit.ToString();
            start.Environment["FAKE_APKSIGNER_VERDICT"] = _verdict;
            start.Environment["ANDROID_SIGNING_STORE_PASS"] = "";
            start.Environment["ANDROID_SIGNING_KEY_PASS"] = "";

            using var process = Process.Start(start) ?? throw new InvalidOperationException("pwsh is not on PATH — the tools/ scripts require PowerShell 7");
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout + await stderr);
        }

        public void Dispose()
        {
            try { Directory.Delete(Dir, recursive: true); } catch (IOException) { /* a scratch dir; the OS temp sweep gets it */ }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !dir.EnumerateFiles("*.slnx").Any()) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
