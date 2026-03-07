using System;
using System.Threading;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;

namespace CommunicateTheSpire2.Stability;

/// <summary>
/// Detects when the game is "stable" (ready for player input) and triggers state publishing.
/// Subscribes to CombatStateChanged and AfterActionExecuted, debounces, and invokes the callback.
/// </summary>
public sealed class StabilityDetector
{
	private readonly Action _onStable;
	private readonly int _debounceMs;
	private CancellationTokenSource? _debounceCts;
	private readonly object _lock = new object();

	public StabilityDetector(Action onStable, int debounceMs = 150)
	{
		_onStable = onStable ?? throw new ArgumentNullException(nameof(onStable));
		_debounceMs = Math.Max(50, debounceMs);
	}

	public void Start()
	{
		CombatManager.Instance.StateTracker.CombatStateChanged += OnCombatStateChanged;
		RunManager.Instance.ActionExecutor.AfterActionExecuted += OnAfterActionExecuted;
	}

	public void Stop()
	{
		CombatManager.Instance.StateTracker.CombatStateChanged -= OnCombatStateChanged;
		RunManager.Instance.ActionExecutor.AfterActionExecuted -= OnAfterActionExecuted;

		lock (_lock)
		{
			_debounceCts?.Cancel();
			_debounceCts = null;
		}
	}

	private void OnCombatStateChanged(CombatState _)
	{
		RequestPublishIfStable();
	}

	private void OnAfterActionExecuted(GameAction _)
	{
		RequestPublishIfStable();
	}

	private void RequestPublishIfStable()
	{
		lock (_lock)
		{
			_debounceCts?.Cancel();
			_debounceCts = new CancellationTokenSource();
			var cts = _debounceCts;
			var token = cts.Token;

			_ = Task.Run(async () =>
			{
				try
				{
					await Task.Delay(_debounceMs, token);
				}
				catch (OperationCanceledException)
				{
					return;
				}

				lock (_lock)
				{
					if (_debounceCts != cts)
						return;
					_debounceCts = null;
				}

				if (IsStable())
				{
					try
					{
						_onStable();
					}
					catch (Exception ex)
					{
						CommunicateTheSpireLog.Write("StabilityDetector callback failed: " + ex);
					}
				}
			}, token);
		}
	}

	/// <summary>True when the game is waiting for player input (combat play phase, no actions running).</summary>
	private static bool IsStable()
	{
		try
		{
			if (!RunManager.Instance.IsInProgress)
				return false;

			if (CombatManager.Instance.IsInProgress)
			{
				return CombatManager.Instance.IsPlayPhase && !RunManager.Instance.ActionExecutor.IsRunning;
			}

			return true;
		}
		catch
		{
			return false;
		}
	}
}
