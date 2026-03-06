using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CommunicateTheSpire2.Ipc;

public sealed class StdioProcessHost : IDisposable
{
	private readonly object _lock = new object();

	private Process? _process;
	private CancellationTokenSource? _cts;
	private Task? _stdoutTask;
	private Task? _stderrTask;
	private Task? _stdinTask;
	private readonly BlockingCollection<string> _stdinLines = new BlockingCollection<string>(new ConcurrentQueue<string>());

	public bool IsRunning
	{
		get
		{
			lock (_lock)
			{
				return _process != null && !_process.HasExited;
			}
		}
	}

	public event Action<string>? StdoutLine;
	public event Action<string>? StderrLine;
	public event Action<int?>? Exited;

	public Task<bool> StartAsync(
		string controllerCommand,
		string? workingDirectory,
		TimeSpan handshakeTimeout,
		Func<string, bool> isReadyLine,
		CancellationToken ct)
	{
		lock (_lock)
		{
			if (_process != null)
				throw new InvalidOperationException("Process already started.");
		}

		var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		_cts = linkedCts;

		var psi = BuildStartInfo(controllerCommand, workingDirectory);
		var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
		process.Exited += (_, _) => Exited?.Invoke(TryGetExitCode(process));

		if (!process.Start())
			throw new InvalidOperationException("Failed to start controller process.");

		lock (_lock)
		{
			_process = process;
		}

		_stdoutTask = Task.Run(() => ReadLinesLoop(process.StandardOutput, StdoutLine, linkedCts.Token), linkedCts.Token);
		_stderrTask = Task.Run(() => ReadLinesLoop(process.StandardError, StderrLine, linkedCts.Token), linkedCts.Token);
		_stdinTask = Task.Run(() => WriteLinesLoop(process.StandardInput, linkedCts.Token), linkedCts.Token);

		return WaitForReadyAsync(handshakeTimeout, isReadyLine, linkedCts.Token);
	}

	public void SendLine(string line)
	{
		_stdinLines.Add(line);
	}

	public void Stop()
	{
		Process? p;
		CancellationTokenSource? cts;
		lock (_lock)
		{
			p = _process;
			_process = null;
			cts = _cts;
			_cts = null;
		}

		try { cts?.Cancel(); } catch { /* ignore */ }
		try { cts?.Dispose(); } catch { /* ignore */ }

		try
		{
			if (p != null && !p.HasExited)
			{
				p.Kill(entireProcessTree: true);
			}
		}
		catch
		{
			// ignore
		}
		finally
		{
			try { p?.Dispose(); } catch { /* ignore */ }
		}
	}

	public void Dispose()
	{
		Stop();
		_stdinLines.Dispose();
	}

	private static ProcessStartInfo BuildStartInfo(string controllerCommand, string? workingDirectory)
	{
		(string fileName, string arguments) = SplitCommand(controllerCommand);

		var psi = new ProcessStartInfo
		{
			FileName = fileName,
			Arguments = arguments,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true
		};

		if (!string.IsNullOrWhiteSpace(workingDirectory))
		{
			psi.WorkingDirectory = workingDirectory!;
		}

		return psi;
	}

	private async Task<bool> WaitForReadyAsync(TimeSpan timeout, Func<string, bool> isReadyLine, CancellationToken ct)
	{
		var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		void Handler(string line)
		{
			if (isReadyLine(line))
			{
				tcs.TrySetResult(true);
			}
		}

		StdoutLine += Handler;
		try
		{
			Task completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
			if (completed == tcs.Task)
				return true;
			return false;
		}
		finally
		{
			StdoutLine -= Handler;
		}
	}

	private static async Task ReadLinesLoop(StreamReader reader, Action<string>? onLine, CancellationToken ct)
	{
		try
		{
			while (!ct.IsCancellationRequested)
			{
				string? line = await reader.ReadLineAsync();
				if (line == null)
				{
					await Task.Delay(10, ct);
					continue;
				}
				onLine?.Invoke(line);
			}
		}
		catch (OperationCanceledException)
		{
			// expected
		}
		catch
		{
			// swallow; host will treat controller as gone
		}
	}

	private void WriteLinesLoop(StreamWriter writer, CancellationToken ct)
	{
		try
		{
			while (!ct.IsCancellationRequested)
			{
				string line = _stdinLines.Take(ct);
				writer.WriteLine(line);
				writer.Flush();
			}
		}
		catch (OperationCanceledException)
		{
			// expected
		}
		catch
		{
			// ignore
		}
	}

	private static int? TryGetExitCode(Process p)
	{
		try
		{
			return p.HasExited ? p.ExitCode : null;
		}
		catch
		{
			return null;
		}
	}

	private static (string fileName, string arguments) SplitCommand(string commandLine)
	{
		// Minimal Windows-friendly command splitting:
		// - If it starts with a quote, treat first quoted segment as executable.
		// - Otherwise, first whitespace-delimited token is executable.
		commandLine = (commandLine ?? "").Trim();
		if (commandLine.Length == 0)
			return ("", "");

		if (commandLine[0] == '"')
		{
			int end = commandLine.IndexOf('"', 1);
			if (end > 1)
			{
				string file = commandLine.Substring(1, end - 1);
				string args = commandLine.Substring(end + 1).Trim();
				return (file, args);
			}
		}

		int firstSpace = commandLine.IndexOf(' ');
		if (firstSpace < 0)
			return (commandLine, "");

		return (commandLine.Substring(0, firstSpace), commandLine.Substring(firstSpace + 1).Trim());
	}
}

