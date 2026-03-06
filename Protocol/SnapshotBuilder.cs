using System;
using CommunicateTheSpire2.Protocol;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Runs;

namespace CommunicateTheSpire2.Protocol;

public static class SnapshotBuilder
{
	public static StateMessage BuildState()
	{
		var msg = new StateMessage();

		msg.in_run = RunManager.Instance.IsInProgress;
		msg.in_combat = CombatManager.Instance.IsInProgress;

		RunState? runState = RunManager.Instance.DebugOnlyGetState();
		if (runState != null)
		{
			msg.run = new RunSummary
			{
				act_index = runState.CurrentActIndex,
				act_floor = runState.ActFloor,
				total_floor = runState.TotalFloor,
				ascension = runState.AscensionLevel,
				gold = runState.Players.Count > 0 ? runState.Players[0].Gold : 0,
				room_type = runState.CurrentRoom?.RoomType.ToString()
			};
		}

		CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
		if (combatState != null && CombatManager.Instance.IsInProgress)
		{
			var combat = new CombatSummary
			{
				round_number = combatState.RoundNumber,
				current_side = combatState.CurrentSide.ToString()
			};

			Player? me = SafeGetLocalPlayer(combatState);
			if (me != null)
			{
				combat.local_player = new PlayerSummary
				{
					net_id = me.NetId,
					current_hp = me.Creature.CurrentHp,
					max_hp = me.Creature.MaxHp,
					block = me.Creature.Block,
					energy = me.PlayerCombatState?.Energy ?? 0,
					stars = me.PlayerCombatState?.Stars ?? 0
				};
			}

			foreach (Creature enemy in combatState.Enemies)
			{
				combat.enemies.Add(new EnemySummary
				{
					combat_id = enemy.CombatId,
					name = enemy.Name,
					current_hp = enemy.CurrentHp,
					max_hp = enemy.MaxHp,
					block = enemy.Block
				});
			}

			msg.combat = combat;
		}

		return msg;
	}

	private static Player? SafeGetLocalPlayer(CombatState combatState)
	{
		try
		{
			return LocalContext.GetMe(combatState);
		}
		catch
		{
			// If LocalContext isn't initialized for some reason, fall back to the first player.
			if (combatState.Players.Count > 0)
				return combatState.Players[0];
			return null;
		}
	}
}

