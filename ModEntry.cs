using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using CommunicateTheSpire2.Config;
using CommunicateTheSpire2.Ipc;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;

namespace CommunicateTheSpire2;

[ModInitializer("Init")]
public static class ModEntry
{
	private static readonly object HostLock = new object();
	private static StdioProcessHost? _host;
	private static CancellationTokenSource? _hostCts;

	static ModEntry()
	{
		// Runs when this type is first used (before Init). Proves the mod DLL was loaded by ModManager.
		DemoModLog.Write("CommunicateTheSpire2 assembly loaded; ModEntry static constructor ran (ModManager is initializing this mod).");
	}

	public static void Init()
	{
		try
		{
			DemoModLog.Write("Init() entered — ModManager called our initializer.");
			Log.Info("CommunicateTheSpire2 loaded! (transport bootstrap in progress)");

			AppDomain.CurrentDomain.ProcessExit += (_, _) => StopController();

			var cfg = CommunicateTheSpire2Config.LoadOrCreateDefault();
			DemoModLog.Write($"Loaded config from {CommunicateTheSpire2Config.ConfigPath} (Enabled={cfg.Enabled}, HandshakeTimeoutSeconds={cfg.HandshakeTimeoutSeconds})");

			if (cfg.Enabled && !string.IsNullOrWhiteSpace(cfg.ControllerCommand))
			{
				StartControllerAsync(cfg);
			}
			else
			{
				DemoModLog.Write("Controller not started (disabled or empty ControllerCommand).");
			}

			// Apply our Harmony patches (game only runs PatchAll when there is no ModInitializer, so we do it here).
			var harmony = new Harmony("CommunicateTheSpire2");
			harmony.PatchAll(Assembly.GetExecutingAssembly());
			DemoModLog.Write("Init() completed successfully.");
		}
		catch (Exception ex)
		{
			DemoModLog.Write("Init() FAILED: " + ex);
			throw;
		}
	}

	private static void StartControllerAsync(CommunicateTheSpire2Config cfg)
	{
		lock (HostLock)
		{
			if (_host != null)
			{
				DemoModLog.Write("Controller already running; skipping start.");
				return;
			}
			_host = new StdioProcessHost();
			_hostCts = new CancellationTokenSource();
			_host.StdoutLine += line =>
			{
				if (cfg.VerboseProtocolLogs)
				{
					DemoModLog.Write("[controller stdout] " + line);
				}
			};
			_host.StderrLine += line =>
			{
				DemoModLog.Write("[controller stderr] " + line);
			};
			_host.Exited += code =>
			{
				DemoModLog.Write($"Controller exited (code={code?.ToString() ?? "?"}).");
				StopController();
			};
		}

		Task.Run(async () =>
		{
			try
			{
				DemoModLog.Write("Starting controller: " + cfg.ControllerCommand);
				bool ready = await _host!.StartAsync(
					cfg.ControllerCommand,
					cfg.ControllerWorkingDirectory,
					TimeSpan.FromSeconds(Math.Max(1, cfg.HandshakeTimeoutSeconds)),
					line => string.Equals(line.Trim(), "ready", StringComparison.OrdinalIgnoreCase),
					_hostCts!.Token);

				if (!ready)
				{
					DemoModLog.Write("Controller handshake timed out (did not receive 'ready'). Stopping controller.");
					StopController();
					return;
				}

				DemoModLog.Write("Controller handshake OK (received 'ready').");
			}
			catch (Exception ex)
			{
				DemoModLog.Write("Controller start FAILED: " + ex);
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
		}

		try { cts?.Cancel(); } catch { /* ignore */ }
		try { cts?.Dispose(); } catch { /* ignore */ }
		try { host?.Dispose(); } catch { /* ignore */ }
	}
}

