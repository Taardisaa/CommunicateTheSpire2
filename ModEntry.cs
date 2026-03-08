using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CommunicateTheSpire2.Choice;
using CommunicateTheSpire2.Commands;
using CommunicateTheSpire2.Stability;
using CommunicateTheSpire2.Config;
using CommunicateTheSpire2.Ipc;
using CommunicateTheSpire2.Protocol;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Logging;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;

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

				if (!_protocolActive)
					return;

				// CHOOSE_RESPONSE must run immediately on this (background) thread. The game blocks the main
				// thread in GetResult() waiting for the choice; if we defer, main thread never runs the
				// deferred callback → deadlock. TryHandleResponse only touches TCS; no Godot access.
				if (IpcChoiceBridge.TryHandleResponse(line))
					return;

				// All other commands need main thread: BuildState() and commands access Godot scene tree.
				string captured = line;
				Callable.From(() => HandleControllerLine(captured)).CallDeferred();
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

	/// <summary>Reload config from file and restart controller if enabled. Call after in-game config save.</summary>
	public static void ApplyConfigFromFile()
	{
		var cfg = CommunicateTheSpire2Config.LoadOrCreateDefault();
		_config = cfg;
		_restartAttempts = 0;
		StopController();
		_shutdownRequested = false; // Reset so we can start again (Save = intentional restart, not app shutdown)
		if (cfg.Enabled && cfg.IsSpawnMode && !string.IsNullOrWhiteSpace(cfg.Command))
			StartControllerAsync(cfg, false);
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
			string logArgs = string.IsNullOrWhiteSpace(args) ? "" : " " + args.Trim();
			CommunicateTheSpireLog.Write($"[CMD] {cmd}{logArgs}");

			switch (cmd)
			{
				case "STATE":
					{
						var state = SnapshotBuilder.BuildState();
						LogStateChecksumIfVerbose(state);
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
				case "POTION":
					{
						if (!TryParsePotionArgs(args ?? "", out string? subCmd, out int slot, out int? targetIndex, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						if (string.Equals(subCmd, "use", StringComparison.OrdinalIgnoreCase))
						{
							if (CommandExecutor.TryExecutePotionUse(slot, targetIndex, out error))
								SendJson(new { type = "potion_use_queued", ok = true, slot, target = targetIndex });
							else if (error != null)
								SendJson(error);
						}
						else if (string.Equals(subCmd, "discard", StringComparison.OrdinalIgnoreCase))
						{
							if (CommandExecutor.TryExecutePotionDiscard(slot, out error))
								SendJson(new { type = "potion_discard_queued", ok = true, slot });
							else if (error != null)
								SendJson(error);
						}
						else
						{
							SendJson(new ErrorMessage { error = "InvalidPotionSubcommand", details = "POTION requires 'use <slot> [target]' or 'discard <slot>'." });
						}
						break;
					}
				case "PROCEED":
					{
						if (CommandExecutor.TryExecuteProceed(out error))
							SendJson(new { type = "proceed_queued", ok = true });
						else if (error != null)
							SendJson(error);
						break;
					}
				case "RETURN":
					{
						if (CommandExecutor.TryExecuteReturn(out error))
							SendJson(new { type = "return_queued", ok = true });
						else if (error != null)
							SendJson(error);
						break;
					}
				case "KEY":
					{
						string keyName = (args ?? "").Trim().ToUpperInvariant();
						if (string.IsNullOrEmpty(keyName))
						{
							SendJson(new ErrorMessage { error = "InvalidKey", details = "KEY requires a key name, e.g. KEY CONFIRM or KEY MAP." });
							break;
						}
						if (CommandExecutor.TryExecuteKey(keyName, out error))
							SendJson(new { type = "key_queued", ok = true, key = keyName });
						else if (error != null)
							SendJson(error);
						break;
					}
				case "CLICK":
					{
						if (!TryParseClickArgs(args ?? "", out string? clickButton, out float clickX, out float clickY, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						if (CommandExecutor.TryExecuteClick(clickButton!, clickX, clickY, out error))
							SendJson(new { type = "click_queued", ok = true, button = clickButton, x = clickX, y = clickY });
						else if (error != null)
							SendJson(error);
						break;
					}
				case "WAIT":
					{
						if (!TryParseWaitArgs(args ?? "", out int waitFrames, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						ScheduleStateAfterWait(waitFrames);
						SendJson(new { type = "wait_queued", ok = true, frames = waitFrames });
						break;
					}
				case "SHOP_BUY_CARD":
					{
						if (!TryParseShopBuyIndex(args ?? "", out int cardIndex, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						DeferShopBuyCard(cardIndex);
						break;
					}
				case "SHOP_BUY_RELIC":
					{
						if (!TryParseShopBuyIndex(args ?? "", out int relicIndex, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						DeferShopBuyRelic(relicIndex);
						break;
					}
				case "SHOP_BUY_POTION":
					{
						if (!TryParseShopBuyIndex(args ?? "", out int potionIndex, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						DeferShopBuyPotion(potionIndex);
						break;
					}
				case "SHOP_PURGE":
					DeferShopPurge();
					break;
				case "REWARD_CHOOSE":
					{
						if (!TryParseRewardChooseArgs(args ?? "", out int rewardIndex, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						if (CommandExecutor.TryExecuteRewardChoose(rewardIndex, out error))
							SendJson(new { type = "reward_choose_queued", ok = true, index = rewardIndex });
						else if (error != null)
							SendJson(error);
						break;
					}
				case "BOSS_REWARD_CHOOSE":
					{
						if (!TryParseBossRewardChooseArgs(args ?? "", out int bossRewardIndex, out error))
						{
							if (error != null)
								SendJson(error);
							break;
						}
						if (CommandExecutor.TryExecuteBossRewardChoose(bossRewardIndex, out error))
							SendJson(new { type = "boss_reward_choose_queued", ok = true, index = bossRewardIndex });
						else if (error != null)
							SendJson(error);
						break;
					}
				case "START":
					{
						TryParseStartArgs(args ?? "", out string? charArg, out string? seedArg, out int ascension);
						if (CommandExecutor.TryExecuteStart(charArg, seedArg, ascension, out error))
							SendJson(new { type = "start_queued", ok = true, character = charArg, seed = seedArg, ascension });
						else if (error != null)
							SendJson(error);
						break;
					}
				default:
					SendJson(new ErrorMessage
					{
						error = "UnknownCommand",
						details = $"Command '{command}' is not supported. Supported: STATE, PING, END, PLAY, EVENT_CHOOSE, REST_CHOOSE, MAP_CHOOSE, POTION, PROCEED, RETURN, KEY, CLICK, WAIT, START, REWARD_CHOOSE, BOSS_REWARD_CHOOSE, SHOP_BUY_CARD, SHOP_BUY_RELIC, SHOP_BUY_POTION, SHOP_PURGE. For choice screens, respond with CHOOSE_RESPONSE <choice_id> <index> or skip."
					});
					break;
			}
		}
		catch (Exception ex)
		{
			SendJson(new ErrorMessage { error = "CommandHandlingError", details = ex.Message });
		}
	}

	/// <summary>Parses START args: "[character] [seed] [ascension]". Character: index 0-4 or id. Ascension 0-20.</summary>
	private static void TryParseStartArgs(string args, out string? characterArg, out string? seedArg, out int ascension)
	{
		characterArg = null;
		seedArg = null;
		ascension = 0;
		string[] parts = string.IsNullOrWhiteSpace(args) ? Array.Empty<string>() : args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length >= 1)
			characterArg = parts[0];
		if (parts.Length >= 2)
		{
			if (int.TryParse(parts[1], out int a2) && parts.Length == 2)
				ascension = Math.Clamp(a2, 0, 20);
			else
				seedArg = parts[1];
		}
		if (parts.Length >= 3 && int.TryParse(parts[2], out int a3))
			ascension = Math.Clamp(a3, 0, 20);
	}

	/// <summary>Parses POTION args: "use &lt;slot&gt; [targetIndex]" or "discard &lt;slot&gt;".</summary>
	private static bool TryParsePotionArgs(string args, out string? subCmd, out int slot, out int? targetIndex, out ErrorMessage? error)
	{
		subCmd = null;
		slot = 0;
		targetIndex = null;
		error = null;
		string[] parts = string.IsNullOrWhiteSpace(args) ? Array.Empty<string>() : args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2)
		{
			error = new ErrorMessage { error = "InvalidPotionArgs", details = "POTION requires 'use <slot> [target]' or 'discard <slot>'." };
			return false;
		}
		subCmd = parts[0];
		if (!int.TryParse(parts[1], out slot) || slot < 0)
		{
			error = new ErrorMessage { error = "InvalidPotionSlot", details = "POTION slot must be a non-negative integer." };
			return false;
		}
		if (parts.Length >= 3)
		{
			if (!int.TryParse(parts[2], out int t))
			{
				error = new ErrorMessage { error = "InvalidTargetIndex", details = "POTION use target index must be an integer." };
				return false;
			}
			targetIndex = t;
		}
		return true;
	}

	private static bool TryParseWaitArgs(string args, out int frames, out ErrorMessage? error)
	{
		frames = 0;
		error = null;
		string[] parts = string.IsNullOrWhiteSpace(args) ? Array.Empty<string>() : args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			error = new ErrorMessage { error = "InvalidWaitArgs", details = "WAIT requires frame count. Usage: WAIT <frames> (e.g. WAIT 60 for ~1 sec at 60fps)." };
			return false;
		}
		if (!int.TryParse(parts[0], out frames) || frames < 0)
		{
			error = new ErrorMessage { error = "InvalidWaitArgs", details = "WAIT frames must be a non-negative integer." };
			return false;
		}
		return true;
	}

	private static void ScheduleStateAfterWait(int frames)
	{
		// ~60fps: N frames ≈ N * 17ms
		int delayMs = Math.Max(0, frames * 17);
		_ = Task.Run(async () =>
		{
			try
			{
				await Task.Delay(delayMs);
				PublishStateIfActive();
			}
			catch (Exception ex)
			{
				CommunicateTheSpireLog.Write("WAIT schedule failed: " + ex);
			}
		});
	}

	private static bool TryParseRewardChooseArgs(string args, out int index, out ErrorMessage? error)
	{
		index = 0;
		error = null;
		string[] parts = string.IsNullOrWhiteSpace(args) ? Array.Empty<string>() : args.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			error = new ErrorMessage { error = "InvalidRewardChooseArgs", details = "REWARD_CHOOSE requires an index. Usage: REWARD_CHOOSE <index> (index from state.rewards[].index)." };
			return false;
		}
		if (!int.TryParse(parts[0], out index) || index < 0)
		{
			error = new ErrorMessage { error = "InvalidRewardChooseArgs", details = "Reward index must be a non-negative integer." };
			return false;
		}
		return true;
	}

	private static bool TryParseBossRewardChooseArgs(string args, out int index, out ErrorMessage? error)
	{
		index = 0;
		error = null;
		string[] parts = string.IsNullOrWhiteSpace(args) ? Array.Empty<string>() : args.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			error = new ErrorMessage { error = "InvalidBossRewardChooseArgs", details = "BOSS_REWARD_CHOOSE requires an index. Usage: BOSS_REWARD_CHOOSE <index> (index from state.boss_reward[].index)." };
			return false;
		}
		if (!int.TryParse(parts[0], out index) || index < 0)
		{
			error = new ErrorMessage { error = "InvalidBossRewardChooseArgs", details = "Boss reward index must be a non-negative integer." };
			return false;
		}
		return true;
	}

	private static bool TryParseShopBuyIndex(string args, out int index, out ErrorMessage? error)
	{
		index = 0;
		error = null;
		string[] parts = string.IsNullOrWhiteSpace(args) ? Array.Empty<string>() : args.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length == 0)
		{
			error = new ErrorMessage { error = "InvalidShopBuyArgs", details = "SHOP_BUY_CARD/RELIC/POTION require an index. Usage: SHOP_BUY_CARD <index> (index from state.shop.cards[].index)." };
			return false;
		}
		if (!int.TryParse(parts[0], out index) || index < 0)
		{
			error = new ErrorMessage { error = "InvalidShopBuyArgs", details = "Shop buy index must be a non-negative integer." };
			return false;
		}
		return true;
	}

	private static void DeferShopBuyCard(int index)
	{
		Callable.From(() => RunShopBuyCardAsync(index)).CallDeferred();
	}

	private static void DeferShopBuyRelic(int index)
	{
		Callable.From(() => RunShopBuyRelicAsync(index)).CallDeferred();
	}

	private static void DeferShopBuyPotion(int index)
	{
		Callable.From(() => RunShopBuyPotionAsync(index)).CallDeferred();
	}

	private static void DeferShopPurge()
	{
		Callable.From(RunShopPurgeAsync).CallDeferred();
	}

	private static async void RunShopBuyCardAsync(int index)
	{
		try
		{
			var (success, err) = await CommandExecutor.TryExecuteShopBuyCardAsync(index);
			if (err != null)
				SendJson(err);
			else
				SendJson(new { type = "shop_buy_ok", item_type = "card", index });
		}
		catch (Exception ex)
		{
			SendJson(new ErrorMessage { error = "ShopBuyError", details = ex.Message });
		}
	}

	private static async void RunShopBuyRelicAsync(int index)
	{
		try
		{
			var (success, err) = await CommandExecutor.TryExecuteShopBuyRelicAsync(index);
			if (err != null)
				SendJson(err);
			else
				SendJson(new { type = "shop_buy_ok", item_type = "relic", index });
		}
		catch (Exception ex)
		{
			SendJson(new ErrorMessage { error = "ShopBuyError", details = ex.Message });
		}
	}

	private static async void RunShopBuyPotionAsync(int index)
	{
		try
		{
			var (success, err) = await CommandExecutor.TryExecuteShopBuyPotionAsync(index);
			if (err != null)
				SendJson(err);
			else
				SendJson(new { type = "shop_buy_ok", item_type = "potion", index });
		}
		catch (Exception ex)
		{
			SendJson(new ErrorMessage { error = "ShopBuyError", details = ex.Message });
		}
	}

	private static async void RunShopPurgeAsync()
	{
		try
		{
			var (success, err) = await CommandExecutor.TryExecuteShopPurgeAsync();
			if (err != null)
				SendJson(err);
			else
				SendJson(new { type = "shop_buy_ok", item_type = "purge" });
		}
		catch (Exception ex)
		{
			SendJson(new ErrorMessage { error = "ShopBuyError", details = ex.Message });
		}
	}

	private static bool TryParseClickArgs(string args, out string? button, out float x, out float y, out ErrorMessage? error)
	{
		button = null;
		x = 0;
		y = 0;
		error = null;
		string[] parts = string.IsNullOrWhiteSpace(args) ? Array.Empty<string>() : args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 3)
		{
			error = new ErrorMessage { error = "InvalidClickArgs", details = "CLICK requires Left|Right X Y. Usage: CLICK Left 960 540 (1920×1080 reference)." };
			return false;
		}
		button = parts[0];
		if (!float.TryParse(parts[1], out x) || !float.TryParse(parts[2], out y))
		{
			error = new ErrorMessage { error = "InvalidClickArgs", details = "CLICK X and Y must be numbers. Usage: CLICK Left 960 540." };
			return false;
		}
		return true;
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
		LogStateChecksumIfVerbose(state);
		SendJson(state);
	}

	private static void LogStateChecksumIfVerbose(StateMessage state)
	{
		try
		{
			string screen = state.screen ?? "null";
			bool inCombat = state.in_combat;
			int availCount = state.available_commands?.Count ?? 0;
			var cmds = state.available_commands ?? new System.Collections.Generic.List<string>();
			string availPreview = availCount <= 5
				? string.Join(",", cmds)
				: $"{availCount} cmds";
			string summary = $"[STATE] screen={screen} in_combat={inCombat} available=[{availPreview}]";
			if (_config?.VerboseProtocolLogs == true)
			{
				string json = ProtocolJson.Serialize(state);
				string checksum = ComputeShortHash(json);
				summary += $" hash={checksum}";
			}
			CommunicateTheSpireLog.Write(summary);
		}
		catch
		{
			// ignore
		}
	}

	private static string ComputeShortHash(string input)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(input);
		byte[] hash = SHA256.HashData(bytes);
		return Convert.ToHexString(hash).AsSpan(0, Math.Min(16, hash.Length * 2)).ToString();
	}

	private static void SendJson(object message)
	{
		SendJsonToController(message);
	}

	/// <summary>Defer card reward click simulation to main thread (called from choice callback).</summary>
	internal static void DeferSimulateCardRewardClick(int[] indices, bool skip)
	{
		Callable.From(() => SimulateCardRewardClick(indices, skip)).CallDeferred();
	}

	private static void SimulateCardRewardClick(int[] indices, bool skip)
	{
		if (NOverlayStack.Instance?.Peek() is not NCardRewardSelectionScreen screen)
			return;
		var holders = UiHelper.FindAll<NCardHolder>(screen);
		if (holders.Count == 0)
			return;
		if (skip || indices == null || indices.Length == 0)
			return;
		int idx = indices[0];
		if (idx < 0 || idx >= holders.Count)
			return;
		holders[idx].EmitSignal(NCardHolder.SignalName.Pressed, holders[idx]);
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

