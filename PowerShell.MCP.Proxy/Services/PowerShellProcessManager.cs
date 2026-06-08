using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PowerShell.MCP.Proxy.Models;

namespace PowerShell.MCP.Proxy.Services;

public class PowerShellProcessManager
{
    private const string PowerShellExecutableName = "pwsh";

    /// <summary>
    /// Checks if a PowerShell process is running
    /// </summary>
    /// <returns>true if PowerShell process is found</returns>
    public static bool IsPowerShellProcessRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName(PowerShellExecutableName);
            var found = processes.Length > 0;

            // Release process object resources
            foreach (var process in processes)
            {
                process.Dispose();
            }

            return found;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error checking PowerShell process: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Starts PowerShell process with PowerShell.MCP module imported
    /// </summary>
    /// <param name="agentId">Agent ID for console isolation</param>
    /// <param name="startupCommands">Optional PowerShell commands to execute after module import (e.g. Write-Host statements)</param>
    /// <returns>true if startup succeeded</returns>
    public static async Task<bool> StartPowerShellWithModuleAsync(string agentId, string? startupCommands = null)
    {
        var (success, _) = await StartPowerShellWithModuleAndPipeNameAsync(agentId, startupCommands);
        return success;
    }

    /// <summary>
    /// Starts PowerShell process with PowerShell.MCP module imported and returns pipe name
    /// </summary>
    /// <param name="agentId">Agent ID for console isolation</param>
    /// <param name="startupCommands">Optional PowerShell commands to execute after module import (e.g. Write-Host statements)</param>
    /// <param name="startLocation">Starting directory path</param>
    /// <returns>Tuple of (success, pipeName)</returns>
    public static async Task<(bool Success, string PipeName)> StartPowerShellWithModuleAndPipeNameAsync(string agentId, string? startupCommands = null, string? startLocation = null)
    {
        int pid = 0;

        // Decide launch strategy. The Wave launcher (hosts pwsh in a Wave Terminal
        // block) and the macOS/Linux terminal launchers don't hand back the pwsh
        // PID, so the new pipe is discovered by polling. Only the native Windows
        // console launcher returns a PID for deterministic pipe-name construction.
        bool useWave = PwshLauncherWave.ShouldUseWave(out _);
        bool isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        bool usePoll = useWave || !isWindows;

        // Poll-based strategies: capture existing pipes BEFORE launching so the new
        // standby pipe can be told apart from pre-existing ones.
        HashSet<string>? existingPipes = null;
        if (usePoll)
        {
            var sessionManager = ConsoleSessionManager.Instance;
            existingPipes = sessionManager.EnumeratePipes(sessionManager.ProxyPid, agentId).ToHashSet();
        }

        string? blockId = null;
        if (useWave)
        {
            var launched = PwshLauncherWave.LaunchPwsh(agentId, startupCommands, startLocation, out blockId);
            if (!launched)
            {
                // Wave launch failed — fall back to a native console window. That
                // yields a real PID, so switch back to deterministic pipe naming.
                pid = PwshLauncherWindows.LaunchPwsh(agentId, startupCommands, startLocation);
                existingPipes = null;
                blockId = null;
            }
            // On success pid stays 0 and existingPipes is retained → poll path below.
        }
        else if (isWindows)
        {
            pid = PwshLauncherWindows.LaunchPwsh(agentId, startupCommands, startLocation);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            PwshLauncherMacOS.LaunchPwsh(agentId, startupCommands, startLocation);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            PwshLauncherLinux.LaunchPwsh(agentId, startupCommands, startLocation);
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported operating system");
        }

        // Wait for Named Pipe to be ready
        string? pipeName;
        if (pid != 0)
        {
            // Windows: We know the PID, construct pipe name with proxy PID, agent ID, and pwsh PID
            var proxyPid = Process.GetCurrentProcess().Id;
            pipeName = ConsoleSessionManager.GetPipeNameForPids(proxyPid, agentId, pid);
        }
        else
        {
            // macOS/Linux: Poll for a NEW standby pipe (exclude existing pipes)
            pipeName = await WaitForNewStandbyPipeAsync(agentId, existingPipes!, maxWaitSeconds: 30);
            if (pipeName == null)
            {
                return (false, string.Empty);
            }
        }

        // Wave-hosted consoles: remember which block hosts this pwsh so the proxy can
        // later mirror the assigned nickname into the block's frame:title header. The
        // pwsh PID is the last segment of the (now-known) pipe name. No-op off Wave
        // (blockId stays null) and for the native fallback (cleared above).
        if (blockId != null)
        {
            var pwshPid = ConsoleSessionManager.GetPidFromPipeName(pipeName);
            if (pwshPid.HasValue)
                ConsoleSessionManager.Instance.SetBlockId(pwshPid.Value, blockId);
        }

        var success = await NamedPipeClient.WaitForPipeReadyAsync(pipeName);

        return (success, pipeName);
    }

    /// <summary>
    /// Waits for a NEW standby pipe to become available (for macOS/Linux)
    /// Polls every 500ms until a new standby pipe is found or timeout
    /// </summary>
    private static async Task<string?> WaitForNewStandbyPipeAsync(string agentId, HashSet<string> existingPipes, int maxWaitSeconds)
    {
        var endTime = DateTime.UtcNow.AddSeconds(maxWaitSeconds);

        while (DateTime.UtcNow < endTime)
        {
            var pipe = await FindNewStandbyPipeAsync(agentId, existingPipes);
            if (pipe != null)
            {
                return pipe;
            }

            await Task.Delay(500);
        }

        return null;
    }

    /// <summary>
    /// Finds a NEW standby pipe from available pipes (excludes existing pipes)
    /// </summary>
    private static async Task<string?> FindNewStandbyPipeAsync(string agentId, HashSet<string> existingPipes)
    {
        var client = new NamedPipeClient();

        foreach (var pipe in ConsoleSessionManager.Instance.EnumeratePipes(ConsoleSessionManager.Instance.ProxyPid, agentId))
        {
            // Skip pipes that existed before launching
            if (existingPipes.Contains(pipe))
            {
                continue;
            }

            try
            {
                var request = "{\"name\":\"get_status\"}";
                var response = await client.SendRequestToAsync(pipe, request);

                using var doc = System.Text.Json.JsonDocument.Parse(response);
                var status = doc.RootElement.GetProperty("status").GetString();

                if (status == PipeStatus.Standby || status == PipeStatus.Completed)
                {
                    return pipe;
                }
            }
            catch
            {
                // Skip this pipe (dead or unresponsive)
            }
        }
        return null;
    }
}

/// <summary>
/// PowerShell snippets shared across platform-specific launchers.
/// </summary>
internal static class PwshLauncherShared
{
    // Install-PSResource on case-sensitive file systems (Linux, case-sensitive APFS on macOS)
    // may create the module directory as 'powershell.mcp'. Rename it so Import-Module can
    // locate the PascalCase name. No-op on case-insensitive file systems.
    internal const string ModuleCaseFix = "foreach ($p in ($env:PSModulePath -split [IO.Path]::PathSeparator)) { if ([string]::IsNullOrWhiteSpace($p)) { continue }; $lc = Join-Path $p 'powershell.mcp'; $uc = Join-Path $p 'PowerShell.MCP'; if ((Test-Path $lc) -and -not (Test-Path $uc)) { Rename-Item $lc $uc; break } }; ";

    // Builds the PowerShell initialization command shared by every non-Windows launcher.
    // The actual delivery to pwsh differs per platform — macOS writes this to /tmp and
    // launches with `-File`, Linux Base64-encodes it for `-EncodedCommand` because of
    // shell quoting through `sh -c` — but the script body is identical. Single quotes
    // inside agentId / startLocation are escaped per PowerShell's '' convention so a
    // future ID format change can't break the launch line.
    // Kept internal so unit tests can lock in shell-safety without spawning a process.
    internal static string BuildInitCommand(int proxyPid, string agentId, string? startupCommands, string? startLocation)
    {
        var setLocation = string.IsNullOrEmpty(startLocation)
            ? "Set-Location ~; "
            : $"Set-Location -LiteralPath '{startLocation.Replace("'", "''")}'; ";

        var escapedAgentId = agentId.Replace("'", "''");
        var core = $"{setLocation}$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{escapedAgentId}'; {ModuleCaseFix}Import-Module PowerShell.MCP -Force; Remove-Module PSReadLine -ErrorAction SilentlyContinue";
        return string.IsNullOrEmpty(startupCommands) ? core : $"{core}; {startupCommands}";
    }

    // Name of the env var that, when set, points the launched console at an explicit
    // module (path to a .psd1 / .dll) instead of resolving 'PowerShell.MCP' off
    // PSModulePath. Lets a dev build run as its own MCP without colliding with an
    // installed PowerShell.MCP module of the same name.
    internal const string ModulePathEnvVar = "POWERSHELL_MCP_MODULE_PATH";

    // Reads POWERSHELL_MCP_MODULE_PATH; returns null when unset/blank (production default).
    internal static string? ResolveModulePath()
    {
        var p = Environment.GetEnvironmentVariable(ModulePathEnvVar);
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    // Builds the Import-Module fragment. Explicit path → `Import-Module '<path>' -Force`
    // (single quotes doubled per PowerShell); otherwise the by-name default.
    internal static string BuildModuleImport(string? modulePath) =>
        string.IsNullOrEmpty(modulePath)
            ? "Import-Module PowerShell.MCP -Force"
            : $"Import-Module '{modulePath.Replace("'", "''")}' -Force";

    // Windows-flavored init body shared by the native console launcher (delivered via
    // -Command) and the Wave launcher (delivered via a temp -File script). Sets the
    // proxy/agent globals the module needs to name its pipe, imports the module, and
    // KEEPS PSReadLine (Wave blocks and Windows consoles are real PTYs — unlike the
    // non-Windows path which strips PSReadLine). cwd is established externally
    // (CreateProcessW lpCurrentDirectory / Wave cmd:cwd), so no Set-Location here.
    internal static string BuildWindowsInitBody(int proxyPid, string agentId, string? startupCommands, string? modulePath)
    {
        var escapedAgentId = agentId.Replace("'", "''");
        var moduleImport = BuildModuleImport(modulePath);
        var core = $"$global:PowerShellMCPProxyPid = {proxyPid}; $global:PowerShellMCPAgentId = '{escapedAgentId}'; {moduleImport}; Import-Module PSReadLine";
        return string.IsNullOrEmpty(startupCommands) ? core : $"{core}; {startupCommands}";
    }
}

/// <summary>
/// Windows-specific launcher using Win32 API to create a new console window
/// </summary>
public static class PwshLauncherWindows
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOW lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFOW
    {
        public uint cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public uint dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    private const uint TOKEN_QUERY = 0x0008;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NEW_CONSOLE = 0x00000010;

    public static int LaunchPwsh(string agentId, string? startupCommands = null, string? startLocation = null)
    {
        IntPtr hToken = IntPtr.Zero;
        IntPtr env = IntPtr.Zero;
        IntPtr hProcess = IntPtr.Zero;
        IntPtr hThread = IntPtr.Zero;
        int pid = 0;

        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out hToken))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            // false = do not inherit current process environment
            // This uses only system/user default environment variables (Control Panel settings)
            if (!CreateEnvironmentBlock(out env, hToken, false))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var si = new STARTUPINFOW { cb = (uint)Marshal.SizeOf<STARTUPINFOW>() };
            var pi = new PROCESS_INFORMATION();

            // Build command with optional startup commands (pre-built Write-Host statements).
            // Set global variables with proxy PID and agent ID before importing module.
            // Honors POWERSHELL_MCP_MODULE_PATH so a dev build imports its own module.
            var proxyPid = Process.GetCurrentProcess().Id;
            var modulePath = PwshLauncherShared.ResolveModulePath();
            var command = PwshLauncherShared.BuildWindowsInitBody(proxyPid, agentId, startupCommands, modulePath);
            string commandLine = $"pwsh.exe -NoExit -Command \"{command}\"";

            bool ok = CreateProcessW(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_UNICODE_ENVIRONMENT | CREATE_NEW_CONSOLE,
                env,
                startLocation ?? userProfile,
                ref si,
                out pi);

            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

            pid = (int)pi.dwProcessId;
            hProcess = pi.hProcess;
            hThread = pi.hThread;
        }
        finally
        {
            if (env != IntPtr.Zero)
                DestroyEnvironmentBlock(env);

            if (hToken != IntPtr.Zero)
                CloseHandle(hToken);

            if (hProcess != IntPtr.Zero)
                CloseHandle(hProcess);

            if (hThread != IntPtr.Zero)
                CloseHandle(hThread);
        }

        return pid;
    }
}

/// <summary>
/// Windows launcher that hosts pwsh inside a NEW Wave Terminal block (via `wsh run`)
/// instead of a standalone console window. Used when the proxy runs under Wave Terminal.
/// The Wave-hosted pwsh is a child of Wave's server (not the proxy), so — like the
/// macOS/Linux launchers — we don't get its PID and the caller discovers the pipe by
/// polling. The launched pwsh sets the same proxy/agent globals, so its named pipe is
/// still discoverable by EnumeratePipes(ProxyPid, agentId).
/// </summary>
public static class PwshLauncherWave
{
    private const string UseWaveEnvVar = "POWERSHELL_MCP_USE_WAVE";

    private static readonly Regex UuidRegex = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    /// <summary>
    /// Decides whether start_console should host pwsh in a Wave block.
    /// auto (default): Wave iff on Windows, running under Wave (WAVETERM set), and wsh.exe
    /// is locatable. POWERSHELL_MCP_USE_WAVE=0/false/off forces native; =1/true/on forces
    /// Wave intent but still requires a locatable wsh (warns and returns false otherwise).
    /// </summary>
    internal static bool ShouldUseWave(out string? wshPath)
    {
        wshPath = null;

        // Wave hosting is Windows-only for now; the macOS/Linux paths already open
        // their own native terminals.
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return false;

        var overrideVal = Environment.GetEnvironmentVariable(UseWaveEnvVar)?.Trim().ToLowerInvariant();
        bool forceOff = overrideVal is "0" or "false" or "off";
        bool forceOn = overrideVal is "1" or "true" or "on";

        if (forceOff)
            return false;

        // auto: require a Wave environment. forceOn skips this gate but still needs wsh.
        if (!forceOn && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAVETERM")))
            return false;

        wshPath = ResolveWshPath();
        if (wshPath == null)
        {
            if (forceOn)
                Console.Error.WriteLine($"[WARN] {UseWaveEnvVar} requested Wave mode but wsh.exe was not found; falling back to a native console.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Locates wsh.exe: WAVETERM_WSHBINDIR first (Wave exports this), then PATH. Null if absent.
    /// </summary>
    internal static string? ResolveWshPath()
    {
        var binDir = Environment.GetEnvironmentVariable("WAVETERM_WSHBINDIR");
        if (!string.IsNullOrEmpty(binDir))
        {
            var candidate = Path.Combine(binDir, "wsh.exe");
            if (File.Exists(candidate))
                return candidate;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathVar))
        {
            foreach (var dir in pathVar.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(dir))
                    continue;
                try
                {
                    var candidate = Path.Combine(dir.Trim(), "wsh.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Skip malformed PATH entries (e.g. invalid path chars).
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the self-deleting temp init script the Wave-hosted pwsh runs via -File.
    /// Windows-flavored (keeps PSReadLine; no Unix ModuleCaseFix / Remove-Module). cwd is
    /// also set in-script as a belt-and-suspenders complement to `wsh run --cwd`.
    /// </summary>
    internal static string BuildWaveInitScript(int proxyPid, string agentId, string? startupCommands, string? startLocation, string? modulePath)
    {
        var setLocation = string.IsNullOrEmpty(startLocation)
            ? string.Empty
            : $"Set-Location -LiteralPath '{startLocation.Replace("'", "''")}'; ";
        var body = PwshLauncherShared.BuildWindowsInitBody(proxyPid, agentId, startupCommands, modulePath);

        // Self-delete on the first line so no script debris survives the launch.
        return $"Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue{Environment.NewLine}{setLocation}{body}";
    }

    /// <summary>
    /// Builds the argv (after wsh.exe) for `wsh run`, which hosts pwsh in a new Wave block and
    /// — unlike `wsh createblock` — actually launches the command server-side (createblock sets
    /// the cmd meta but never triggers the run). Everything after `--` is passed to pwsh as
    /// plain argv (no shell, no JSON), so the Windows temp path needs no escaping.
    ///
    /// -NoProfile keeps the AI console clean: the native MCP launcher relies on the user's
    /// profile self-detecting the proxy as its parent process to fast-path, but a Wave-hosted
    /// pwsh is parented by the Wave server, so that detection can't fire. Skipping the profile
    /// avoids its startup cost and side effects (Set-Location, Starship, etc.) while preserving
    /// PATH, which is inherited from the environment rather than set by the profile.
    /// </summary>
    internal static List<string> BuildWshRunArgs(string tempFile, string cwd)
    {
        return new List<string>
        {
            "run",
            "--cwd", cwd,
            "--",
            "pwsh.exe",
            "-NoProfile",
            "-NoExit",
            "-File", tempFile,
        };
    }

    /// <summary>
    /// Builds the argv (after wsh.exe) for `wsh setmeta`, which writes the block's
    /// <c>frame:title</c> — the override Wave renders in the block header (alongside
    /// frame:icon / frame:text). Everything is passed as plain argv (no shell), so the
    /// "#PID Name" title — which contains '#' and a space — needs no escaping.
    /// Kept internal + pure so a unit test can lock the argv without spawning wsh.
    /// </summary>
    internal static List<string> BuildSetMetaTitleArgs(string blockId, string title)
    {
        return new List<string>
        {
            "setmeta",
            "-b", blockId,
            $"frame:title={title}",
        };
    }

    /// <summary>
    /// Mirrors the console's assigned nickname into its Wave block header via
    /// `wsh setmeta -b &lt;blockId&gt; frame:title=&lt;title&gt;`. Best-effort and idempotent:
    /// the pipe-based <c>$Host.UI.RawUI.WindowTitle</c> (status line / other terminals'
    /// tabs) remains the source of truth; this only adds Wave's block header, which
    /// ignores the OSC/console title. Any failure (no wsh, locked-down Wave, dead block)
    /// is logged and swallowed so it can never break console startup. No-op when blockId
    /// is null/empty or wsh.exe can't be located.
    /// </summary>
    public static async Task SetBlockTitleAsync(string? blockId, string title)
    {
        if (string.IsNullOrEmpty(blockId)) return;

        var wshPath = ResolveWshPath();
        if (wshPath == null) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = wshPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in BuildSetMetaTitleArgs(blockId, title))
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null) return;

            // Drain both streams to avoid a pipe-buffer deadlock, then bound the wait.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                Console.Error.WriteLine("[WARN] wsh setmeta (frame:title) timed out.");
                return;
            }

            if (process.ExitCode != 0)
            {
                var stderr = (await stderrTask).Trim();
                Console.Error.WriteLine($"[WARN] wsh setmeta (frame:title) exited {process.ExitCode}: {stderr}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] wsh setmeta (frame:title) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Launches pwsh inside a new Wave block via `wsh run`. Returns true on success (blockId
    /// parsed from stdout). On ANY failure logs to stderr and returns false so the caller can
    /// fall back to a native console. `wsh run` returns as soon as the block is created (it
    /// does not wait for the command), so a failure is known synchronously within the wsh
    /// timeout — we do NOT wait for the named pipe here; the caller polls for it.
    /// </summary>
    public static bool LaunchPwsh(string agentId, string? startupCommands, string? startLocation, out string? blockId)
    {
        blockId = null;

        var wshPath = ResolveWshPath();
        if (wshPath == null)
        {
            Console.Error.WriteLine("[WARN] Wave launch requested but wsh.exe was not found; falling back to a native console.");
            return false;
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cwd = (!string.IsNullOrEmpty(startLocation) && Directory.Exists(startLocation))
            ? startLocation
            : userProfile;

        var proxyPid = Process.GetCurrentProcess().Id;
        var modulePath = PwshLauncherShared.ResolveModulePath();
        var script = BuildWaveInitScript(proxyPid, agentId, startupCommands, startLocation, modulePath);

        var tempFile = Path.Combine(Path.GetTempPath(), $"pwsh-mcp-wave-init-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(tempFile, script);

            var psi = new ProcessStartInfo
            {
                FileName = wshPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var arg in BuildWshRunArgs(tempFile, cwd))
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
            {
                Console.Error.WriteLine("[WARN] Failed to start wsh; falling back to a native console.");
                TryDeleteTemp(tempFile);
                return false;
            }

            // Drain both streams asynchronously to avoid a pipe-buffer deadlock.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(10000))
            {
                Console.Error.WriteLine("[WARN] wsh run timed out; falling back to a native console.");
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                TryDeleteTemp(tempFile);
                return false;
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0)
            {
                Console.Error.WriteLine($"[WARN] wsh run exited {process.ExitCode}: {stderr.Trim()}; falling back to a native console.");
                TryDeleteTemp(tempFile);
                return false;
            }

            var match = UuidRegex.Match(stdout);
            blockId = match.Success ? match.Value : null;
            Console.Error.WriteLine($"[INFO] Wave block created for agent '{agentId}' (blockId={blockId ?? "unknown"}).");

            // The block's pwsh self-deletes the temp script on its first line; no cleanup here.
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[WARN] Wave launch failed: {ex.Message}; falling back to a native console.");
            TryDeleteTemp(tempFile);
            return false;
        }
    }

    private static void TryDeleteTemp(string tempFile)
    {
        try
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
        catch
        {
            // Best effort — an orphaned init script in %TEMP% is harmless.
        }
    }
}

/// <summary>
/// macOS-specific launcher using AppleScript to open Terminal.app
/// </summary>
public static class PwshLauncherMacOS
{
    public static void LaunchPwsh(string agentId, string? startupCommands = null, string? startLocation = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        var proxyPid = Process.GetCurrentProcess().Id;
        var initCommand = PwshLauncherShared.BuildInitCommand(proxyPid, agentId, startupCommands, startLocation);

        // Write the init to a temp .ps1 and launch with `-File`. AppleScript's `do script`
        // types its argument into the Terminal window verbatim — if we passed the PS source
        // directly (or a Base64 -EncodedCommand) the user would see ~1KB of noise scroll past
        // every time a console starts. A short `-File /tmp/xyz.ps1` stays readable.
        // The script self-deletes on first line so there is no long-lived debris.
        // We intentionally use /tmp (world-known, symlink-resolved, ASCII-safe in AppleScript
        // string context) rather than $TMPDIR which is per-user and varies across setups.
        var tempFile = $"/tmp/pwsh-mcp-init-{Guid.NewGuid():N}.ps1";
        var scriptBody = $"Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue{Environment.NewLine}{initCommand}";
        File.WriteAllText(tempFile, scriptBody);

        using var process = Process.Start(psi);
        if (process != null)
        {
            process.StandardInput.WriteLine("tell application \"Terminal\"");
            process.StandardInput.WriteLine("    activate");
            process.StandardInput.WriteLine($"    do script \"pwsh -NoExit -File '{tempFile}'\"");
            process.StandardInput.WriteLine("end tell");
            process.StandardInput.Close();
            process.WaitForExit(5000);
        }
    }

}

/// <summary>
/// Linux-specific launcher that tries multiple terminal emulators
/// </summary>
public static class PwshLauncherLinux
{
    // Terminal emulator configurations: (name, useShellWrapper, args...)
    // useShellWrapper: true = use "sh -c" to wrap the command (for terminals that need a single command string)
    private static readonly string[] SupportedTerminals =
    [
        "gnome-terminal",
        "konsole",
        "xfce4-terminal",
        "xterm",
        "lxterminal",
        "mate-terminal",
        "terminator",
        "tilix",
        "alacritty",
        "kitty",
    ];

    public static void LaunchPwsh(string agentId, string? startupCommands = null, string? startLocation = null)
    {
        foreach (var terminal in SupportedTerminals)
        {
            if (TryLaunchTerminal(terminal, agentId, startupCommands, startLocation))
            {
                return;
            }
        }

        // No terminal emulator found - launch pwsh directly (headless/CI mode)
        Console.Error.WriteLine("[INFO] No terminal emulator found, launching pwsh directly");
        LaunchPwshDirectly(agentId, startupCommands, startLocation);
    }

    private static bool TryLaunchTerminal(string terminal, string agentId, string? startupCommands, string? startLocation)
    {
        try
        {
            // Check if terminal exists using 'which'
            var whichPsi = new ProcessStartInfo
            {
                FileName = "which",
                Arguments = terminal,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var whichProcess = Process.Start(whichPsi);
            whichProcess?.WaitForExit(2000);

            if (whichProcess?.ExitCode != 0)
            {
                return false;
            }

            // Get user's default shell
            var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";

            // Launch terminal via setsid to detach from parent process
            // We use the user's login shell to ensure ~/.bash_profile or ~/.zprofile is loaded,
            // which sets up PATH and other environment variables correctly.
            // This mimics what happens when a user manually opens a terminal and types 'pwsh'.
            var psi = new ProcessStartInfo
            {
                FileName = "setsid",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            // Build the PowerShell init script (shared with macOS) and encode to Base64.
            // Linux delivers it via -EncodedCommand because the launch path goes through
            // `setsid <terminal> ... <shell> -l -c '<pwshCommand>'` and quoting through
            // multiple shell layers is fragile; -EncodedCommand sidesteps that entirely.
            var proxyPid = Process.GetCurrentProcess().Id;
            var initCommand = PwshLauncherShared.BuildInitCommand(proxyPid, agentId, startupCommands, startLocation);
            var encodedCommand = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(initCommand));

            // Command to launch pwsh with encoded initialization via login shell
            // exec replaces the shell with pwsh to keep the process tree clean
            var pwshCommand = $"exec pwsh -NoExit -EncodedCommand {encodedCommand}";

            // setsid <terminal> ... <shell> -l -c '<pwshCommand>'
            psi.ArgumentList.Add(terminal);

            // Configure arguments based on terminal type
            switch (terminal)
            {
                case "gnome-terminal":
                    psi.ArgumentList.Add("--");
                    psi.ArgumentList.Add(shell);
                    psi.ArgumentList.Add("-l");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(pwshCommand);
                    break;

                case "konsole":
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add(shell);
                    psi.ArgumentList.Add("-l");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(pwshCommand);
                    break;

                case "xterm":
                case "lxterminal":
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add(shell);
                    psi.ArgumentList.Add("-l");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(pwshCommand);
                    break;

                case "xfce4-terminal":
                case "mate-terminal":
                case "terminator":
                case "tilix":
                    // These terminals expect -e with a single command string
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add($"{shell} -l -c '{pwshCommand}'");
                    break;

                case "alacritty":
                    psi.ArgumentList.Add("-e");
                    psi.ArgumentList.Add(shell);
                    psi.ArgumentList.Add("-l");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(pwshCommand);
                    break;

                case "kitty":
                    psi.ArgumentList.Add(shell);
                    psi.ArgumentList.Add("-l");
                    psi.ArgumentList.Add("-c");
                    psi.ArgumentList.Add(pwshCommand);
                    break;

                default:
                    return false;
            }

            Process.Start(psi)?.Dispose();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Launches pwsh directly without a terminal emulator (for CI/headless environments)
    /// </summary>
    private static void LaunchPwshDirectly(string agentId, string? startupCommands, string? startLocation)
    {
        // Reuse the shared init builder so a single source of truth (with proper
        // single-quote escaping) covers terminal / headless / macOS paths. ArgumentList
        // delivers -Command as a separate argv entry, so no Base64 encoding is needed
        // here even though the terminal-launched Linux path uses -EncodedCommand.
        // BuildInitCommand prepends Set-Location, so the headless cwd is established
        // by the script body itself; -WorkingDirectory is no longer needed.
        var proxyPid = Process.GetCurrentProcess().Id;
        var initCommand = PwshLauncherShared.BuildInitCommand(proxyPid, agentId, startupCommands, startLocation);

        var psi = new ProcessStartInfo
        {
            FileName = "pwsh",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-NoExit");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(initCommand);

        var process = Process.Start(psi);
        if (process != null)
        {
            // Drain stdout/stderr asynchronously to prevent buffer deadlocks
            // Log to stderr (which is separate from MCP stdio transport)
            process.OutputDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine($"[HEADLESS] {e.Data}"); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.Error.WriteLine($"[HEADLESS-ERR] {e.Data}"); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
    }
}
