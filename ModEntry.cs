using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommunicateTheSpire2.Choice;
using CommunicateTheSpire2.Commands;
using CommunicateTheSpire2.Stability;
using CommunicateTheSpire2.Config;
using CommunicateTheSpire2.Ipc;
using CommunicateTheSpire2.Protocol;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CommunicateTheSpire2;

[ModInitializer("Init")]
public static class ModEntry
{
	private static readonly object HostLock = new object();
	private static StdioProcessHost? _host;
	private static CancellationTokenSource? _hostCts;
	private static CommunicateTheSpire2Config? _config;
	private static bool _shutdownRequested;
	private static int _restartAttempts;
	private static volatile bool _protocolActive;
	private static StabilityDetector? _stabilityDetector;

	static ModEntry()
	{
		// Runs when this type is first used (before Init). Proves the mod DLL was loaded by ModManager.
		CommunicateTheSpireLog.Write("CommunicateTheSpire2 assembly loaded; ModEntry static constructor ran (ModManager is initializing this mod).");
	}

	public static void Init()
	{
		try
		{
			CommunicateTheSpireLog.Write("Init() entered — ModManager called our initializer.");
			Log.Info("CommunicateTheSpire2 loaded! (transport bootstrap in progress)");

			AppDomain.CurrentDomain.ProcessExit += (_, _) => StopController();

			var cfg = CommunicateTheSpire2Config.LoadOrCreateDefault();
			_config = cfg;
			_shutdownRequested = false;
			_restartAttempts = 0;
			CommunicateTheSpireLog.Write(
				$"Loaded config from {CommunicateTheSpire2Config.ConfigPath} " +
				$"(Enabled={cfg.Enabled}, Mode={cfg.Mode}, HandshakeTimeoutSeconds={cfg.HandshakeTimeoutSeconds}, VerboseProtocolLogs={cfg.VerboseProtocolLogs}, RestartOnExit={cfg.RestartOnExit}, MaxRestartAttempts={cfg.MaxRestartAttempts}, RestartBackoffMs={cfg.RestartBackoffMs})");
			CommunicateTheSpireLog.Write($"Controller stderr log path: {CommunicateTheSpireLog.ControllerErrorLogPath}");

			if (!cfg.Enabled)
			{
				CommunicateTheSpireLog.Write("Controller not started (config enabled=false).");
			}
			else if (!cfg.IsSpawnMode)
			{
				CommunicateTheSpireLog.Write($"Controller mode '{cfg.Mode}' is not implemented yet. Set mode='spawn' to launch a controller process.");
			}
			else if (string.IsNullOrWhiteSpace(cfg.Command))
			{
				CommunicateTheSpireLog.Write("Controller not started (mode=spawn but command is empty).");
			}
			else
			{
				StartControllerAsync(cfg, false);
				CardSelectCmd.PushSelector(new IpcCardSelector());
			}

			// Apply our Harmony patches (game only runs PatchAll when there is no ModInitializer, so we do it here).
			var harmony = new Harmony("CommunicateTheSpire2");
			harmony.PatchAll(Assembly.GetExecutingAssembly());
			CommunicateTheSpireLog.Write("Init() completed successfully.");
		}
		catch (Exception ex)
		{
			CommunicateTheSpireLog.Write("Init() FAILED: " + ex);
			throw;
		}
	}

	private static void StartControllerAsync(CommunicateTheSpire2Config cfg, bool isRestart)
	{
		StdioProcessHost host;
		CancellationTokenSource cts;

		lock (HostLock)
		{
			if (_host != null)
			{
				CommunicateTheSpireLog.Write("Controller already running; skipping start.");
				return;
			}
			if (_shutdownRequested)
			{
				CommunicateTheSpireLog.Write("Controller start skipped because shutdown is in progress.");
				return;
			}

			host = new StdioProcessHost();
			cts = new CancellationTokenSource();
			_host = host;
			_hostCts = cts;

			host.StdoutLine += line =>
			{
				if (cfg.VerboseProtocolLogs)
				{
					CommunicateTheSpireLog.Write("[controller stdout] " + line);
				}

				// After handshake, treat controller stdout lines as protocol commands.
				if (_protocolActive)
				{
					HandleControllerLine(line);
				}
			};
			host.StderrLine += line =>
			{
				CommunicateTheSpireLog.WriteControllerError(line);
				if (cfg.VerboseProtocolLogs)
				{
					CommunicateTheSpireLog.Write("[controller stderr] " + line);
				}
			};
			host.Exited += code =>
			{
				CommunicateTheSpireLog.Write($"Controller exited (code={code?.ToString() ?? "?"}).");
				HandleHostStopped(host, true, "process exited");
			};
		}

		Task.Run(async () =>
		{
			try
			{
				CommunicateTheSpireLog.Write((isRestart ? "Restarting" : "Starting") + " controller: " + cfg.Command);
				bool ready = await host.StartAsync(
					cfg.Command,
					cfg.WorkingDirectory,
					TimeSpan.FromSeconds(cfg.HandshakeTimeoutSeconds),
					line => string.Equals(line.Trim(), "ready", StringComparison.OrdinalIgnoreCase),
					cts.Token);

				if (!ready)
				{
					CommunicateTheSpireLog.Write("Controller startup failed (handshake timed out or process exited before ready).");
					HandleHostStopped(host, true, "startup failed");
					return;
				}

				CommunicateTheSpireLog.Write("Controller handshake OK (received 'ready').");

				// Enable protocol handling, start stability detector, send initial hello.
				lock (HostLock)
				{
					if (!ReferenceEquals(_host, host))
						return;
					_protocolActive = true;
					_restartAttempts = 0;

					_stabilityDetector?.Stop();
					_stabilityDetector = new StabilityDetector(PublishStateIfActive, debounceMs: 150);
					_stabilityDetector.Start();
				}

				SendJson(new HelloMessage());
			}
			catch (Exception ex)
			{
				CommunicateTheSpireLog.Write("Controller start FAILED: " + ex);
				HandleHostStopped(host, true, "startup exception");
			}
		});
	}

	private static void StopController()
	{
		StdioProcessHost? host;
		CancellationTokenSource? cts;
		lock (HostLock)
		{
			_shutdownRequested = true;
			host = _host;
			cts = _hostCts;
			_host = null;
			_hostCts = null;
			_protocolActive = false;
			_stabilityDetector?.Stop();
			_stabilityDetector = null;
		}

		try { cts?.Cancel(); } catch { /* ignore */ }
		try { cts?.Dispose(); } catch { /* ignore */ }
		try { host?.Dispose(); } catch { /* ignore */ }
		CommunicateTheSpireLog.Write("Controller host stopped.");
	}

	private static void HandleHostStopped(StdioProcessHost host, bool considerRestart, string reason)
	{
		CancellationTokenSource? cts = null;
		bool shouldRestart = false;
		int restartAttempt = 0;
		int restartDelayMs = 0;
		CommunicateTheSpire2Config? cfg = null;

		lock (HostLock)
		{
			if (!ReferenceEquals(_host, host))
				return;

			cts = _hostCts;
			_hostCts = null;
			_host = null;
			_protocolActive = false;
			_stabilityDetector?.Stop();
			_stabilityDetector = null;

			cfg = _config;
			if (!_shutdownRequested &&
				considerRestart &&
				cfg != null &&
				cfg.Enabled &&
				cfg.IsSpawnMode &&
				cfg.RestartOnExit &&
				_restartAttempts < cfg.MaxRestartAttempts)
			{
				_restartAttempts++;
				restartAttempt = _restartAttempts;
				restartDelayMs = cfg.RestartBackoffMs;
				shouldRestart = true;
			}
		}

		try { cts?.Cancel(); } catch { /* ignore */ }
		try { cts?.Dispose(); } catch { /* ignore */ }
		try { host.Dispose(); } catch { /* ignore */ }

		CommunicateTheSpireLog.Write($"Controller host transitioned to stopped ({reason}).");

		if (!shouldRestart || cfg == null)
			return;

		CommunicateTheSpireLog.Write($"Scheduling controller restart attempt {restartAttempt}/{cfg.MaxRestartAttempts} in {restartDelayMs} ms.");
		Task.Run(async () =>
		{
			try
			{
				await Task.Delay(restartDelayMs);
				lock (HostLock)
				{
					if (_shutdownRequested || _host != null)
						return;
				}
				StartControllerAsync(cfg, true);
			}
			catch (Exception ex)
			{
				CommunicateTheSpireLog.Write("Restart scheduling failed: " + ex);
			}
		});
	}

	private static void HandleControllerLine(string line)
	{
		try
		{
			if (IpcChoiceBridge.TryHandleResponse(line))
				return;

			if (!ProtocolCommandParser.TryParse(line, out string command, out string? _, out string? args, out ErrorMessage? error))
			{
				if (error != null)
					SendJson(error);
				return;
			}

			string cmd = command.ToUpperInvariant();
			switch (cmd)
			{
				case "STATE":
					{
						var state = SnapshotBuilder.BuildState();
						SendJson(state);
						break;
					}
				case "PING":
					{
						SendJson(new PongMessage());
						break;
					}
				case "END":
					{
						if (CommandExecutor.TryExecuteEnd(out error))
							SendJson(new { type = "end_queued", ok = true });
						else if (error != null)
							SendJson(error);
						break;
					}
				case "PLAY":
					{
						int handIndex;
						int? targetIndex = null;
						if (!TryParsePlayArgs(args ?? "", out handIndex, out targetIndex, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						if (CommandExecutor.TryExecutePlay(handIndex, targetIndex, out error))
							SendJson(new { type = "play_queued", ok = true, hand_index = handIndex });
						else if (error != null)
							SendJson(error);
						break;
					}
				case "EVENT_CHOOSE":
					{
						if (!TryParseSingleIntArgs(args ?? "", "EVENT_CHOOSE", out int idx, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						if (CommandExecutor.TryExecuteEventChoose(idx, out error))
							SendJson(new { type = "event_choose_queued", ok = true, index = idx });
						else if (error != null)
							SendJson(error);
						break;
					}
				case "REST_CHOOSE":
					{
						if (!TryParseSingleIntArgs(args ?? "", "REST_CHOOSE", out int idx, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						if (CommandExecutor.TryExecuteRestChoose(idx, out error))
							SendJson(new { type = "rest_choose_queued", ok = true, index = idx });
						else if (error != null)
							SendJson(error);
						break;
					}
				case "MAP_CHOOSE":
					{
						if (!TryParseSingleIntArgs(args ?? "", "MAP_CHOOSE", out int idx, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						if (CommandExecutor.TryExecuteMapChoose(idx, out error))
							SendJson(new { type = "map_choose_queued", ok = true, index = idx });
						else if (error != null)
							SendJson(error);
						break;
					}
				default:
					SendJson(new ErrorMessage
					{
						error = "UnknownCommand",
						details = $"Command '{command}' is not supported. Supported: STATE, PING, END, PLAY, EVENT_CHOOSE, REST_CHOOSE, MAP_CHOOSE. For choice screens, respond with CHOOSE_RESPONSE <choice_id> <index> or skip."
					});
					break;
			}
		}
		catch (Exception ex)
		{
			SendJson(new ErrorMessage { error = "CommandHandlingError", details = ex.Message });
		}
	}

	private static bool TryParseSingleIntArgs(string args, string cmdName, out int value, out ErrorMessage? error)
	{
		value = 0;
		error = null;
		string[] parts = string.IsNullOrWhiteSpace(args) ? Array.Empty<string>() : args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			error = new ErrorMessage { error = "InvalidArgs", details = $"{cmdName} requires an integer index. Usage: {cmdName} <index>" };
			return false;
		}
		if (!int.TryParse(parts[0], out value))
		{
			error = new ErrorMessage { error = "InvalidArgs", details = $"{cmdName} index must be an integer." };
			return false;
		}
		return true;
	}

	private static bool TryParsePlayArgs(string args, out int handIndex, out int? targetIndex, out ErrorMessage? error)
	{
		handIndex = 0;
		targetIndex = null;
		error = null;

		string[] parts = string.IsNullOrWhiteSpace(args) ? Array.Empty<string>() : args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			error = new ErrorMessage { error = "InvalidArgs", details = "PLAY requires hand index. Usage: PLAY <handIndex> [targetIndex]" };
			return false;
		}
		if (!int.TryParse(parts[0], out handIndex))
		{
			error = new ErrorMessage { error = "InvalidArgs", details = "Hand index must be an integer." };
			return false;
		}
		if (parts.Length >= 2)
		{
			if (!int.TryParse(parts[1], out int t))
			{
				error = new ErrorMessage { error = "InvalidArgs", details = "Target index must be an integer." };
				return false;
			}
			targetIndex = t;
		}

		return true;
	}

	/// <summary>Called by StabilityDetector when the game becomes stable. Sends state if protocol is active.</summary>
	private static void PublishStateIfActive()
	{
		if (!_protocolActive)
			return;

		StdioProcessHost? host;
		lock (HostLock)
		{
			host = _host;
		}
		if (host == null || !host.IsRunning)
			return;

		var state = SnapshotBuilder.BuildState();
		SendJson(state);
	}

	private static void SendJson(object message)
	{
		SendJsonToController(message);
	}

	/// <summary>Called by IpcChoiceBridge to send choice_request. Same assembly, no circular dep.</summary>
	internal static void SendJsonToController(object message)
	{
		StdioProcessHost? host;
		lock (HostLock)
		{
			host = _host;
		}

		if (host == null || !host.IsRunning || !_protocolActive)
			return;

		try
		{
			string json = ProtocolJson.Serialize(message);
			host.SendLine(json);
		}
		catch (Exception ex)
		{
			CommunicateTheSpireLog.Write("Failed to send JSON to controller: " + ex);
		}
	}
}

