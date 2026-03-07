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
	private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

	public static bool IsExpectingResponse
	{
		get
		{
			lock (_lock)
			{
				return _pendingTcs != null;
			}
		}
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
		lock (_lock)
		{
			if (_pendingChoiceId == choiceId && _pendingTcs != null)
			{
				tcs = _pendingTcs;
				_pendingChoiceId = null;
				_pendingTcs = null;
			}
		}
		tcs?.TrySetResult(new ChoiceResult { Indices = indices, Skip = skip });
	}
}
