using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CommunicateTheSpire2.Ipc;

public enum ProcessHostState
{
	Stopped,
	Starting,
	Running,
	Stopping
}

public sealed class StdioProcessHost : IDisposable
{
	private readonly object _lock = new object();

	private Process? _process;
	private CancellationTokenSource? _cts;
	private Task? _stdoutTask;
	private Task? _stderrTask;
	private Task? _stdinTask;
	private readonly BlockingCollection<string> _stdinLines = new BlockingCollection<string>(new ConcurrentQueue<string>());
	private ProcessHostState _state = ProcessHostState.Stopped;

	public bool IsRunning
	{
		get
		{
			lock (_lock)
			{
				return _state == ProcessHostState.Running && _process != null && !_process.HasExited;
			}
		}
	}

	public ProcessHostState State
	{
		get
		{
			lock (_lock)
			{
				return _state;
			}
		}
	}

	public event Action<string>? StdoutLine;
	public event Action<string>? StderrLine;
	public event Action<int?>? Exited;

	public async Task<bool> StartAsync(
		string controllerCommand,
		string? workingDirectory,
		TimeSpan handshakeTimeout,
		Func<string, bool> isReadyLine,
		CancellationToken ct)
	{
		lock (_lock)
		{
			if (_state != ProcessHostState.Stopped)
				throw new InvalidOperationException($"Process host is not stopped (state={_state}).");
			_state = ProcessHostState.Starting;
		}

		var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		_cts = linkedCts;

		var psi = BuildStartInfo(controllerCommand, workingDirectory);
		var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
		process.Exited += (_, _) => Exited?.Invoke(TryGetExitCode(process));

		if (!process.Start())
		{
			lock (_lock)
			{
				_state = ProcessHostState.Stopped;
			}
			throw new InvalidOperationException("Failed to start controller process.");
		}

		lock (_lock)
		{
			_process = process;
		}

		// Subscribe handshake handler BEFORE starting the read loop so we don't miss "ready"
		// if the controller prints it immediately (race: line could be delivered before we subscribe).
		var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
		void ReadyHandler(string line)
		{
			if (isReadyLine(line))
				readyTcs.TrySetResult(true);
		}
		StdoutLine += ReadyHandler;

		_stdoutTask = Task.Run(() => ReadLinesLoop(process.StandardOutput, StdoutLine, linkedCts.Token), linkedCts.Token);
		_stderrTask = Task.Run(() => ReadLinesLoop(process.StandardError, StderrLine, linkedCts.Token), linkedCts.Token);
		_stdinTask = Task.Run(() => WriteLinesLoop(process.StandardInput, linkedCts.Token), linkedCts.Token);

		var result = await CompleteStartupAsync(process, handshakeTimeout, readyTcs.Task, linkedCts.Token);
		StdoutLine -= ReadyHandler;
		return result;
	}

	public void SendLine(string line)
	{
		if (!IsRunning)
			return;
		_stdinLines.Add(line);
	}

	public void Stop()
	{
		Process? p;
		CancellationTokenSource? cts;
		lock (_lock)
		{
			if (_state == ProcessHostState.Stopped)
				return;
			_state = ProcessHostState.Stopping;

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
			lock (_lock)
			{
				_state = ProcessHostState.Stopped;
			}
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

	private async Task<bool> CompleteStartupAsync(Process process, TimeSpan timeout, Task<bool> readyTask, CancellationToken ct)
	{
		bool ready = await WaitForReadyAsync(process, timeout, readyTask, ct);
		if (!ready)
		{
			Stop();
			return false;
		}

		lock (_lock)
		{
			if (_state == ProcessHostState.Starting)
			{
				_state = ProcessHostState.Running;
			}
		}

		return true;
	}

	private async Task<bool> WaitForReadyAsync(Process process, TimeSpan timeout, Task<bool> readyTask, CancellationToken ct)
	{
		try
		{
			Task completed = await Task.WhenAny(
				readyTask,
				Task.Delay(timeout, ct),
				process.WaitForExitAsync(ct));
			if (completed == readyTask && readyTask.IsCompletedSuccessfully && readyTask.Result)
				return true;
			return false;
		}
		catch (OperationCanceledException)
		{
			return false;
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
					break;
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

