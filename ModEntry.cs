using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using CommunicateTheSpire2.Config;
using CommunicateTheSpire2.Ipc;
using CommunicateTheSpire2.Protocol;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CommunicateTheSpire2;

[ModInitializer("Init")]
public static class ModEntry
{
	private static readonly object HostLock = new object();
	private static StdioProcessHost? _host;
	private static CancellationTokenSource? _hostCts;
	private static volatile bool _protocolActive;

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
			CommunicateTheSpireLog.Write(
				$"Loaded config from {CommunicateTheSpire2Config.ConfigPath} " +
				$"(Enabled={cfg.Enabled}, Mode={cfg.Mode}, HandshakeTimeoutSeconds={cfg.HandshakeTimeoutSeconds}, VerboseProtocolLogs={cfg.VerboseProtocolLogs})");
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
				StartControllerAsync(cfg);
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

	private static void StartControllerAsync(CommunicateTheSpire2Config cfg)
	{
		lock (HostLock)
		{
			if (_host != null)
			{
				CommunicateTheSpireLog.Write("Controller already running; skipping start.");
				return;
			}
			_host = new StdioProcessHost();
			_hostCts = new CancellationTokenSource();
			_host.StdoutLine += line =>
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
			_host.StderrLine += line =>
			{
				CommunicateTheSpireLog.WriteControllerError(line);
				if (cfg.VerboseProtocolLogs)
				{
					CommunicateTheSpireLog.Write("[controller stderr] " + line);
				}
			};
			_host.Exited += code =>
			{
				CommunicateTheSpireLog.Write($"Controller exited (code={code?.ToString() ?? "?"}).");
				StopController();
			};
		}

		Task.Run(async () =>
		{
			try
			{
				CommunicateTheSpireLog.Write("Starting controller: " + cfg.Command);
				bool ready = await _host!.StartAsync(
					cfg.Command,
					cfg.WorkingDirectory,
					TimeSpan.FromSeconds(Math.Max(1, cfg.HandshakeTimeoutSeconds)),
					line => string.Equals(line.Trim(), "ready", StringComparison.OrdinalIgnoreCase),
					_hostCts!.Token);

				if (!ready)
				{
					CommunicateTheSpireLog.Write("Controller handshake timed out (did not receive 'ready'). Stopping controller.");
					StopController();
					return;
				}

				CommunicateTheSpireLog.Write("Controller handshake OK (received 'ready').");

				// Enable protocol handling and send initial hello.
				lock (HostLock)
				{
					_protocolActive = true;
				}

				SendJson(new HelloMessage());
			}
			catch (Exception ex)
			{
				CommunicateTheSpireLog.Write("Controller start FAILED: " + ex);
				StopController();
			}
		});
	}

	private static void StopController()
	{
		StdioProcessHost? host;
		CancellationTokenSource? cts;
		lock (HostLock)
		{
			host = _host;
			cts = _hostCts;
			_host = null;
			_hostCts = null;
			_protocolActive = false;
		}

		try { cts?.Cancel(); } catch { /* ignore */ }
		try { cts?.Dispose(); } catch { /* ignore */ }
		try { host?.Dispose(); } catch { /* ignore */ }
	}

	private static void HandleControllerLine(string line)
	{
		try
		{
			if (!ProtocolCommandParser.TryParse(line, out string command, out string? _, out ErrorMessage? error))
			{
				if (error != null)
				{
					SendJson(error);
				}
				return;
			}

			switch (command.ToUpperInvariant())
			{
				case "STATE":
					{
						var state = SnapshotBuilder.BuildState();
						SendJson(state);
						break;
					}
				case "PING":
					{
						var pong = new PongMessage();
						SendJson(pong);
						break;
					}
				default:
					{
						var err = new ErrorMessage
						{
							error = "UnknownCommand",
							details = $"Command '{command}' is not supported."
						};
						SendJson(err);
						break;
					}
			}
		}
		catch (Exception ex)
		{
			var err = new ErrorMessage
			{
				error = "CommandHandlingError",
				details = ex.Message
			};
			SendJson(err);
		}
	}

	private static void SendJson(object message)
	{
		StdioProcessHost? host;
		lock (HostLock)
		{
			host = _host;
		}

		if (host == null || !host.IsRunning)
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

