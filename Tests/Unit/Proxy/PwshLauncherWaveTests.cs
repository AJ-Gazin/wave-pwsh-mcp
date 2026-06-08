using PowerShell.MCP.Proxy.Services;
using Xunit;

namespace PowerShell.MCP.Tests.Unit.Proxy;

// Guards the Wave launcher's spawn-free building blocks: the Windows-flavored init
// body/script, the wsh createblock argv, wsh.exe discovery, and the ShouldUseWave
// decision. Nothing here spawns a process or talks to Wave — all logic is pure
// string/env/filesystem so it runs deterministically in CI.
//
// Wave hosting and several of these helpers are Windows-only; the gated facts skip
// on non-Windows runners. The pure string builders (init body, wsh args) are
// platform-independent and always assert.
public class PwshLauncherWaveTests
{
    private const string DefaultAgentId = "default";
    private const int DefaultPid = 12345;

    // Sets an env var for the duration of a test and restores it on dispose.
    private sealed class EnvScope : IDisposable
    {
        private readonly List<(string Key, string? Original)> _saved = new();

        public EnvScope Set(string key, string? value)
        {
            _saved.Add((key, Environment.GetEnvironmentVariable(key)));
            Environment.SetEnvironmentVariable(key, value);
            return this;
        }

        public void Dispose()
        {
            // Restore in reverse so overlapping keys end at their original value.
            for (int i = _saved.Count - 1; i >= 0; i--)
                Environment.SetEnvironmentVariable(_saved[i].Key, _saved[i].Original);
        }
    }

    // --- BuildWindowsInitBody (shared with the native Windows launcher) -----------

    [Fact]
    public void BuildWindowsInitBody_Default_SetsGlobalsImportsModuleAndKeepsPSReadLine()
    {
        var body = PwshLauncherShared.BuildWindowsInitBody(DefaultPid, DefaultAgentId, null, null);

        Assert.Contains($"$global:PowerShellMCPProxyPid = {DefaultPid}", body);
        Assert.Contains("$global:PowerShellMCPAgentId = 'default'", body);
        Assert.Contains("Import-Module PowerShell.MCP -Force", body);
        Assert.Contains("Import-Module PSReadLine", body);
    }

    [Fact]
    public void BuildWindowsInitBody_DoesNotStripPSReadLineOrRunUnixCaseFix()
    {
        // The Unix init removes PSReadLine and runs a case-fix rename; the Windows
        // flavor (real PTY) must do neither.
        var body = PwshLauncherShared.BuildWindowsInitBody(DefaultPid, DefaultAgentId, null, null);

        Assert.DoesNotContain("Remove-Module PSReadLine", body);
        Assert.DoesNotContain("Rename-Item", body);
    }

    [Fact]
    public void BuildWindowsInitBody_AgentIdWithSingleQuote_IsEscaped()
    {
        var body = PwshLauncherShared.BuildWindowsInitBody(DefaultPid, "te'st", null, null);

        Assert.Contains("$global:PowerShellMCPAgentId = 'te''st'", body);
    }

    [Fact]
    public void BuildWindowsInitBody_ModulePathSet_ImportsByPathNotByName()
    {
        var body = PwshLauncherShared.BuildWindowsInitBody(
            DefaultPid, DefaultAgentId, null, @"C:\dev\PowerShell.MCP.psd1");

        Assert.Contains(@"Import-Module 'C:\dev\PowerShell.MCP.psd1' -Force", body);
        // The by-name default must NOT also appear.
        Assert.DoesNotContain("Import-Module PowerShell.MCP -Force", body);
    }

    [Fact]
    public void BuildWindowsInitBody_ModulePathWithSingleQuote_IsEscaped()
    {
        var body = PwshLauncherShared.BuildWindowsInitBody(
            DefaultPid, DefaultAgentId, null, @"C:\d'ev\mod.psd1");

        Assert.Contains(@"Import-Module 'C:\d''ev\mod.psd1' -Force", body);
    }

    [Fact]
    public void BuildWindowsInitBody_AppendsStartupCommands()
    {
        var body = PwshLauncherShared.BuildWindowsInitBody(
            DefaultPid, DefaultAgentId, "Write-Host 'hi'", null);

        Assert.EndsWith("; Write-Host 'hi'", body);
    }

    [Fact]
    public void BuildWindowsInitBody_NoStartupCommands_HasNoTrailingSeparator()
    {
        var body = PwshLauncherShared.BuildWindowsInitBody(DefaultPid, DefaultAgentId, null, null);

        Assert.EndsWith("Import-Module PSReadLine", body);
    }

    // --- BuildWaveInitScript ------------------------------------------------------

    [Fact]
    public void BuildWaveInitScript_SelfDeletesOnFirstLine()
    {
        var script = PwshLauncherWave.BuildWaveInitScript(DefaultPid, DefaultAgentId, null, null, null);

        var firstLine = script.Split(Environment.NewLine, 2)[0];
        Assert.Equal("Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue", firstLine);
    }

    [Fact]
    public void BuildWaveInitScript_WithStartLocation_EmitsEscapedSetLocation()
    {
        var script = PwshLauncherWave.BuildWaveInitScript(
            DefaultPid, DefaultAgentId, null, @"C:\Users\o'brien", null);

        Assert.Contains(@"Set-Location -LiteralPath 'C:\Users\o''brien';", script);
    }

    [Fact]
    public void BuildWaveInitScript_NoStartLocation_OmitsSetLocation()
    {
        var script = PwshLauncherWave.BuildWaveInitScript(DefaultPid, DefaultAgentId, null, null, null);

        Assert.DoesNotContain("Set-Location", script);
    }

    // --- BuildWshRunArgs ----------------------------------------------------------

    [Fact]
    public void BuildWshRunArgs_ProducesExpectedWshRunArgv()
    {
        var tempFile = @"C:\Users\aj\AppData\Local\Temp\pwsh-mcp-wave-init-abc.ps1";
        var cwd = @"C:\work";

        var args = PwshLauncherWave.BuildWshRunArgs(tempFile, cwd);

        // `wsh run` passes everything after `--` to pwsh as plain argv (no shell, no JSON),
        // so the Windows path is used verbatim — no backslash escaping.
        Assert.Equal(
            new[]
            {
                "run",
                "--cwd", cwd,
                "--",
                "pwsh.exe",
                "-NoProfile",
                "-NoExit",
                "-File", tempFile,
            },
            args);
    }

    // --- ResolveWshPath -----------------------------------------------------------

    [Fact]
    public void ResolveWshPath_PrefersWshBinDir()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var wsh = Path.Combine(dir, "wsh.exe");
            File.WriteAllText(wsh, string.Empty);

            using var _ = new EnvScope()
                .Set("WAVETERM_WSHBINDIR", dir)
                .Set("PATH", string.Empty);

            Assert.Equal(wsh, PwshLauncherWave.ResolveWshPath());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveWshPath_FallsBackToPath()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var wsh = Path.Combine(dir, "wsh.exe");
            File.WriteAllText(wsh, string.Empty);

            using var _ = new EnvScope()
                .Set("WAVETERM_WSHBINDIR", null)
                .Set("PATH", dir);

            Assert.Equal(wsh, PwshLauncherWave.ResolveWshPath());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ResolveWshPath_ReturnsNullWhenAbsent()
    {
        var dir = Directory.CreateTempSubdirectory().FullName; // empty dir, no wsh.exe
        try
        {
            using var _ = new EnvScope()
                .Set("WAVETERM_WSHBINDIR", dir)
                .Set("PATH", dir);

            Assert.Null(PwshLauncherWave.ResolveWshPath());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // --- ShouldUseWave ------------------------------------------------------------

    [Fact]
    public void ShouldUseWave_OverrideOff_ReturnsFalseEvenUnderWave()
    {
        using var _ = new EnvScope()
            .Set("POWERSHELL_MCP_USE_WAVE", "0")
            .Set("WAVETERM", "1");

        Assert.False(PwshLauncherWave.ShouldUseWave(out var wshPath));
        Assert.Null(wshPath);
    }

    [Fact]
    public void ShouldUseWave_Auto_NoWaveterm_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows())
            return; // Wave hosting is Windows-only; the platform gate already returns false.

        using var env = new EnvScope()
            .Set("POWERSHELL_MCP_USE_WAVE", null)
            .Set("WAVETERM", null);

        Assert.False(PwshLauncherWave.ShouldUseWave(out _));
    }

    [Fact]
    public void ShouldUseWave_Auto_UnderWaveWithWsh_ReturnsTrue()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var wsh = Path.Combine(dir, "wsh.exe");
            File.WriteAllText(wsh, string.Empty);

            using var _ = new EnvScope()
                .Set("POWERSHELL_MCP_USE_WAVE", null)
                .Set("WAVETERM", "1")
                .Set("WAVETERM_WSHBINDIR", dir);

            Assert.True(PwshLauncherWave.ShouldUseWave(out var wshPath));
            Assert.Equal(wsh, wshPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ShouldUseWave_Auto_UnderWaveWithoutWsh_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Directory.CreateTempSubdirectory().FullName; // no wsh.exe
        try
        {
            using var _ = new EnvScope()
                .Set("POWERSHELL_MCP_USE_WAVE", null)
                .Set("WAVETERM", "1")
                .Set("WAVETERM_WSHBINDIR", dir)
                .Set("PATH", dir);

            Assert.False(PwshLauncherWave.ShouldUseWave(out var wshPath));
            Assert.Null(wshPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ShouldUseWave_ForceOnWithWsh_ReturnsTrueWithoutWaveterm()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var wsh = Path.Combine(dir, "wsh.exe");
            File.WriteAllText(wsh, string.Empty);

            using var _ = new EnvScope()
                .Set("POWERSHELL_MCP_USE_WAVE", "1")
                .Set("WAVETERM", null) // forceOn skips the WAVETERM gate
                .Set("WAVETERM_WSHBINDIR", dir);

            Assert.True(PwshLauncherWave.ShouldUseWave(out var wshPath));
            Assert.Equal(wsh, wshPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
