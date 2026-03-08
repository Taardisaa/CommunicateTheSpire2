using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunicateTheSpire2.Config;
using CommunicateTheSpire2.Protocol;

namespace CommunicateTheSpire2.Choice;

public sealed class ChoiceResult
{
	public int[] Indices { get; set; } = Array.Empty<int>();
	public bool Skip { get; set; }
}

public static class IpcChoiceBridge
{
	private static readonly object _lock = new object();
	private static string? _pendingChoiceId;
	private static TaskCompletionSource<ChoiceResult>? _pendingTcs;
	private static ManualResetEventSlim? _syncWait;
	private static ChoiceResult? _syncResult;
	private static string? _simulationChoiceId;
	private static Action<int[], bool>? _simulationCallback;
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

	public static bool IsExpectingResponse
	{
		get
		{
			lock (_lock)
			{
				return _pendingTcs != null || _syncWait != null || _simulationCallback != null;
			}
		}
	}

	/// <summary>Request a choice and run a callback when the controller responds (simulation path).
	/// Used when the UI is visible and we simulate a click on the chosen option. Callback runs on the same
	/// thread as CompletePending (often background); delegate should CallDeferred UI work.</summary>
	public static void RequestChoiceSimulation(
		string choiceType,
		IReadOnlyList<ChoiceOptionSummary> options,
		int minSelect,
		int maxSelect,
		IReadOnlyList<string>? alternatives,
		Action<int[], bool> onResponse)
	{
		string choiceId = Guid.NewGuid().ToString("N")[..12];
		lock (_lock)
		{
			if (_pendingTcs != null)
				_pendingTcs.TrySetResult(new ChoiceResult { Skip = true });
			if (_syncWait != null)
			{
				_syncResult = new ChoiceResult { Skip = true };
				_syncWait.Set();
			}
			_pendingChoiceId = choiceId;
			_pendingTcs = null;
			_syncWait = null;
			_syncResult = null;
			_simulationChoiceId = choiceId;
			_simulationCallback = onResponse;
		}

		var msg = new ChoiceRequestMessage
		{
			choice_id = choiceId,
			choice_type = choiceType,
			min_select = minSelect,
			max_select = maxSelect,
			options = options.ToList(),
			alternatives = (alternatives ?? Array.Empty<string>()).ToList()
		};

		CommunicateTheSpireLog.Write($"[CHOICE] {choiceType} id={choiceId} #options={options.Count} min={minSelect} max={maxSelect} (simulation)");
		global::CommunicateTheSpire2.ModEntry.SendJsonToController(msg);
	}

	/// <summary>Sync request for callers that block (e.g. ICardSelector). Uses ManualResetEventSlim so the
	/// background thread can unblock the main thread when CHOOSE_RESPONSE arrives.</summary>
	public static ChoiceResult RequestChoiceSync(
		string choiceType,
		IReadOnlyList<ChoiceOptionSummary> options,
		int minSelect,
		int maxSelect,
		IReadOnlyList<string>? alternatives = null)
	{
		string choiceId = Guid.NewGuid().ToString("N")[..12];
		using var evt = new ManualResetEventSlim(false);
		ChoiceResult? result = null;

		lock (_lock)
		{
			if (_pendingTcs != null)
				_pendingTcs.TrySetResult(new ChoiceResult { Skip = true });
			if (_syncWait != null)
			{
				_syncResult = new ChoiceResult { Skip = true };
				_syncWait.Set();
			}
			_pendingChoiceId = choiceId;
			_pendingTcs = null;
			_syncWait = evt;
			_syncResult = null;
		}

		var msg = new ChoiceRequestMessage
		{
			choice_id = choiceId,
			choice_type = choiceType,
			min_select = minSelect,
			max_select = maxSelect,
			options = options.ToList(),
			alternatives = (alternatives ?? Array.Empty<string>()).ToList()
		};

		CommunicateTheSpireLog.Write($"[CHOICE] {choiceType} id={choiceId} #options={options.Count} min={minSelect} max={maxSelect}");
		global::CommunicateTheSpire2.ModEntry.SendJsonToController(msg);

		var cfg = CommunicateTheSpire2Config.LoadOrCreateDefault();
		var timeoutMs = Math.Max(5000, cfg.HandshakeTimeoutSeconds * 1000 * 3);
		evt.Wait(timeoutMs);

		lock (_lock)
		{
			result = _syncResult;
			if (_syncWait == evt)
				_syncWait = null; // we timed out; CompletePending clears _syncWait on success
		}

		return result ?? new ChoiceResult { Skip = true };
	}

	/// <summary>Request a choice from the controller. Returns indices of selected options, or Skip=true to skip.</summary>
	public static async Task<ChoiceResult> RequestChoiceAsync(
		string choiceType,
		IReadOnlyList<ChoiceOptionSummary> options,
		int minSelect,
		int maxSelect,
		IReadOnlyList<string>? alternatives = null)
	{
		string choiceId = Guid.NewGuid().ToString("N")[..12];
		var tcs = new TaskCompletionSource<ChoiceResult>(TaskCreationOptions.RunContinuationsAsynchronously);

		lock (_lock)
		{
			if (_pendingTcs != null)
			{
				_pendingTcs.TrySetResult(new ChoiceResult { Skip = true });
			}
			_pendingChoiceId = choiceId;
			_pendingTcs = tcs;
		}

		var msg = new ChoiceRequestMessage
		{
			choice_id = choiceId,
			choice_type = choiceType,
			min_select = minSelect,
			max_select = maxSelect,
			options = options.ToList(),
			alternatives = (alternatives ?? Array.Empty<string>()).ToList()
		};

		CommunicateTheSpireLog.Write($"[CHOICE] {choiceType} id={choiceId} #options={options.Count} min={minSelect} max={maxSelect}");
		global::CommunicateTheSpire2.ModEntry.SendJsonToController(msg);

		var cfg = CommunicateTheSpire2Config.LoadOrCreateDefault();
		var timeoutMs = Math.Max(5000, cfg.HandshakeTimeoutSeconds * 1000 * 3);
		var timeoutTask = Task.Delay(timeoutMs);
		var resultTask = tcs.Task;

		try
		{
			var completed = await Task.WhenAny(resultTask, timeoutTask);
			if (completed == timeoutTask)
			{
				tcs.TrySetResult(new ChoiceResult { Skip = true });
				return await resultTask;
			}
			return await resultTask;
		}
		finally
		{
			lock (_lock)
			{
				if (_pendingChoiceId == choiceId)
				{
					_pendingChoiceId = null;
					_pendingTcs = null;
				}
			}
		}
	}

	public static bool TryHandleResponse(string line)
	{
		string trimmed = (line ?? "").Trim();
		if (trimmed.Length == 0)
			return false;

		// Plain: CHOOSE_RESPONSE <choice_id> skip
		// Plain: CHOOSE_RESPONSE <choice_id> 0 2
		if (trimmed.StartsWith("CHOOSE_RESPONSE ", StringComparison.OrdinalIgnoreCase))
		{
			var rest = trimmed.Substring(15).Trim();
			int space = rest.IndexOf(' ');
			string choiceId = space >= 0 ? rest.Substring(0, space).Trim() : rest;
			string remainder = space >= 0 ? rest.Substring(space + 1).Trim() : "";

			if (string.Equals(remainder, "skip", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(remainder))
			{
				CompletePending(choiceId, Array.Empty<int>(), skip: true);
				return true;
			}

			var indices = new List<int>();
			foreach (var part in remainder.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
			{
				if (int.TryParse(part, out int idx))
					indices.Add(idx);
			}
			CompletePending(choiceId, indices.ToArray(), skip: false);
			return true;
		}

		// JSON: {"type":"choice_response","choice_id":"...","indices":[0,2]} or "skip":true
		if (trimmed.StartsWith("{"))
		{
			try
			{
				using var doc = System.Text.Json.JsonDocument.Parse(trimmed);
				var root = doc.RootElement;
				if (!root.TryGetProperty("type", out var typeEl) || typeEl.GetString() != "choice_response")
					return false;
				if (!root.TryGetProperty("choice_id", out var idEl))
					return false;
				string choiceId = idEl.GetString() ?? "";

				if (root.TryGetProperty("skip", out var skipEl) && skipEl.GetBoolean())
				{
					CompletePending(choiceId, Array.Empty<int>(), skip: true);
					return true;
				}

				if (root.TryGetProperty("indices", out var indicesEl))
				{
					var indices = new List<int>();
					foreach (var el in indicesEl.EnumerateArray())
					{
						if (el.TryGetInt32(out int idx))
							indices.Add(idx);
					}
					CompletePending(choiceId, indices.ToArray(), skip: false);
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		return false;
	}

	private static void CompletePending(string choiceId, int[] indices, bool skip)
	{
		TaskCompletionSource<ChoiceResult>? tcs = null;
		ManualResetEventSlim? evt = null;
		Action<int[], bool>? simulationCb = null;
		lock (_lock)
		{
			if (_pendingChoiceId != choiceId)
				return;
			var r = new ChoiceResult { Indices = indices, Skip = skip };
			tcs = _pendingTcs;
			evt = _syncWait;
			if (_simulationChoiceId == choiceId)
			{
				simulationCb = _simulationCallback;
				_simulationChoiceId = null;
				_simulationCallback = null;
			}
			_pendingChoiceId = null;
			_pendingTcs = null;
			_syncWait = null;
			_syncResult = r;
		}
		tcs?.TrySetResult(new ChoiceResult { Indices = indices, Skip = skip });
		evt?.Set();
		try
		{
			simulationCb?.Invoke(indices, skip);
		}
		catch (Exception ex)
		{
			CommunicateTheSpireLog.Write("Choice simulation callback error: " + ex);
		}
	}
}
