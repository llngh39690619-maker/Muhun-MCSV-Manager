#requires -Version 7.4

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FilePath,

    [Parameter(Mandatory = $true)]
    [string]$WorkingDirectory,

    [Parameter(Mandatory = $true)]
    [string[]]$ArgumentList
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not $IsWindows) {
    throw 'An isolated Windows desktop can be created only on Windows.'
}

$resolvedFile = [IO.Path]::GetFullPath($FilePath)
$resolvedWorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
if (-not (Test-Path -LiteralPath $resolvedFile -PathType Leaf)) {
    throw "Isolated process executable was not found: $resolvedFile"
}
if (-not (Test-Path -LiteralPath $resolvedWorkingDirectory -PathType Container)) {
    throw "Isolated process working directory was not found: $resolvedWorkingDirectory"
}

if (-not ('Muhun.Mcsv.IsolatedDesktopProcess' -as [type])) {
    Add-Type -TypeDefinition @'
#nullable enable
using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Muhun.Mcsv;

public static class IsolatedDesktopProcess
{
    private const uint DesktopAllAccess = 0x000F01FF;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint CreateNoWindow = 0x08000000;
    private const uint BelowNormalPriorityClass = 0x00004000;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const int StdInputHandle = -10;

    public static int Run(
        string filePath,
        string[] arguments,
        string workingDirectory,
        string desktopName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(desktopName);

        var desktop = CreateDesktop(
            desktopName,
            IntPtr.Zero,
            IntPtr.Zero,
            0,
            DesktopAllAccess,
            IntPtr.Zero);
        if (desktop == IntPtr.Zero)
        {
            throw NewWin32("Could not create the private test desktop.");
        }

        IntPtr stdoutRead = IntPtr.Zero;
        IntPtr stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero;
        IntPtr stderrWrite = IntPtr.Zero;
        IntPtr stdin = IntPtr.Zero;
        ProcessInformation process = default;
        try
        {
            var inheritable = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                InheritHandle = true,
            };
            CreateCapturedPipe(out stdoutRead, out stdoutWrite, ref inheritable, "stdout");
            CreateCapturedPipe(out stderrRead, out stderrWrite, ref inheritable, "stderr");
            stdin = CreateFile(
                "NUL",
                GenericRead,
                FileShareRead | FileShareWrite,
                ref inheritable,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);
            if (stdin == new IntPtr(-1))
            {
                stdin = IntPtr.Zero;
                throw NewWin32("Could not open an isolated stdin handle.");
            }

            var startup = new StartupInfo
            {
                Size = Marshal.SizeOf<StartupInfo>(),
                Desktop = $"WinSta0\\{desktopName}",
                Flags = StartfUseStdHandles,
                StandardInput = stdin,
                StandardOutput = stdoutWrite,
                StandardError = stderrWrite,
            };
            var commandLine = new StringBuilder(BuildCommandLine(filePath, arguments));
            var previousDesktop = Environment.GetEnvironmentVariable(
                "X_MCSV_ISOLATED_TEST_DESKTOP",
                EnvironmentVariableTarget.Process);
            bool created;
            try
            {
                Environment.SetEnvironmentVariable(
                    "X_MCSV_ISOLATED_TEST_DESKTOP",
                    desktopName,
                    EnvironmentVariableTarget.Process);
                created = CreateProcess(
                    filePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    CreateNoWindow | BelowNormalPriorityClass,
                    IntPtr.Zero,
                    workingDirectory,
                    ref startup,
                    out process);
            }
            finally
            {
                Environment.SetEnvironmentVariable(
                    "X_MCSV_ISOLATED_TEST_DESKTOP",
                    previousDesktop,
                    EnvironmentVariableTarget.Process);
            }

            if (!created)
            {
                throw NewWin32("Could not start the isolated test process.");
            }

            CloseOwned(ref stdoutWrite);
            CloseOwned(ref stderrWrite);
            CloseOwned(ref stdin);

            using var stdoutHandle = new SafeFileHandle(stdoutRead, ownsHandle: true);
            stdoutRead = IntPtr.Zero;
            using var stderrHandle = new SafeFileHandle(stderrRead, ownsHandle: true);
            stderrRead = IntPtr.Zero;
            using var stdoutStream = new FileStream(stdoutHandle, FileAccess.Read, 4096, isAsync: false);
            using var stderrStream = new FileStream(stderrHandle, FileAccess.Read, 4096, isAsync: false);
            using var stdoutReader = new StreamReader(stdoutStream, Encoding.UTF8, true);
            using var stderrReader = new StreamReader(stderrStream, Encoding.UTF8, true);
            var stdoutTask = stdoutReader.ReadToEndAsync();
            var stderrTask = stderrReader.ReadToEndAsync();

            var wait = WaitForSingleObject(process.Process, Infinite);
            if (wait != 0)
            {
                throw NewWin32("Waiting for the isolated test process failed.");
            }

            var stdout = stdoutTask.GetAwaiter().GetResult();
            var stderr = stderrTask.GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(stdout))
            {
                Console.Out.Write(stdout);
            }
            if (!string.IsNullOrEmpty(stderr))
            {
                Console.Error.Write(stderr);
            }

            if (!GetExitCodeProcess(process.Process, out var exitCode))
            {
                throw NewWin32("Could not read the isolated test process exit code.");
            }

            return unchecked((int)exitCode);
        }
        finally
        {
            CloseOwned(ref stdoutRead);
            CloseOwned(ref stdoutWrite);
            CloseOwned(ref stderrRead);
            CloseOwned(ref stderrWrite);
            CloseOwned(ref stdin);
            CloseOwned(ref process.Thread);
            CloseOwned(ref process.Process);
            _ = CloseDesktop(desktop);
        }
    }

    private static void CreateCapturedPipe(
        out IntPtr read,
        out IntPtr write,
        ref SecurityAttributes attributes,
        string label)
    {
        if (!CreatePipe(out read, out write, ref attributes, 0))
        {
            throw NewWin32($"Could not create the isolated {label} pipe.");
        }
        if (!SetHandleInformation(read, HandleFlagInherit, 0))
        {
            throw NewWin32($"Could not protect the isolated {label} read handle.");
        }
    }

    private static string BuildCommandLine(string filePath, string[] arguments)
    {
        var result = new StringBuilder(Quote(filePath));
        foreach (var argument in arguments)
        {
            result.Append(' ').Append(Quote(argument ?? string.Empty));
        }
        return result.ToString();
    }

    private static string Quote(string value)
    {
        if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
        {
            return value;
        }

        var result = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private static Win32Exception NewWin32(string message)
        => new(Marshal.GetLastWin32Error(), message);

    private static void CloseOwned(ref IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            handle = IntPtr.Zero;
            return;
        }
        _ = CloseHandle(handle);
        handle = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2Count;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktop(
        string desktopName,
        IntPtr device,
        IntPtr deviceMode,
        uint flags,
        uint desiredAccess,
        IntPtr securityAttributes);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out IntPtr readPipe,
        out IntPtr writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        IntPtr handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SecurityAttributes securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
'@
}

$desktopName = "X-MCSV-Tests-$PID-$([guid]::NewGuid().ToString('N'))"
$exitCode = [Muhun.Mcsv.IsolatedDesktopProcess]::Run(
    $resolvedFile,
    $ArgumentList,
    $resolvedWorkingDirectory,
    $desktopName)
exit $exitCode
