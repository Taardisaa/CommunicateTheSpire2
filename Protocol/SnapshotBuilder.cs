using System;
using System.Collections.Generic;
using System.Linq;
using CommunicateTheSpire2.Protocol;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;

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

			msg.screen = GetScreen(runState);
			PopulateEventOptions(msg, runState);
			PopulateRestSiteOptions(msg, runState);
			PopulateMapState(msg, runState);
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

			if (me != null && me.PlayerCombatState?.Hand != null)
			{
				var hand = PileType.Hand.GetPile(me).Cards;
				for (int i = 0; i < hand.Count; i++)
				{
					CardModel card = hand[i];
					int energyCost = card.EnergyCost.CostsX ? -1 : card.EnergyCost.GetWithModifiers(CostModifiers.All);
					bool playable = card.CanPlay(out _, out _);
					combat.hand_cards.Add(new HandCardSummary
					{
						index = i,
						id = card.Id.Entry,
						energy_cost = energyCost,
						target_type = card.TargetType.ToString(),
						playable = playable
					});
				}
			}

			msg.combat = combat;
		}

		PopulateAvailableCommands(msg);
		return msg;
	}

	private static void PopulateAvailableCommands(StateMessage msg)
	{
		msg.available_commands.Add("STATE");
		msg.available_commands.Add("PING");

		if (msg.in_combat && msg.combat != null)
		{
			if (msg.combat.current_side == "Player")
			{
				msg.available_commands.Add("END");
				if (msg.combat.hand_cards.Count > 0)
					msg.available_commands.Add("PLAY");
			}
		}

		if (msg.screen == "event" && msg.event_options.Count > 0)
			msg.available_commands.Add("EVENT_CHOOSE");

		if (msg.screen == "rest_site" && msg.rest_site_options.Count > 0)
			msg.available_commands.Add("REST_CHOOSE");

		if (msg.screen == "map" && msg.map != null && msg.map.reachable.Count > 0)
			msg.available_commands.Add("MAP_CHOOSE");
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

	private static string? GetScreen(RunState runState)
	{
		if (CombatManager.Instance.IsInProgress)
			return "combat";

		AbstractRoom? room = runState.CurrentRoom;
		if (room == null)
			return null;

		return room.RoomType switch
		{
			RoomType.Event => "event",
			RoomType.RestSite => "rest_site",
			RoomType.Map => "map",
			RoomType.Shop => "shop",
			RoomType.Treasure => "treasure",
			RoomType.Monster or RoomType.Elite or RoomType.Boss => "combat",
			_ => "unknown"
		};
	}

	private static void PopulateEventOptions(StateMessage msg, RunState runState)
	{
		if (runState.CurrentRoom is not EventRoom)
			return;

		try
		{
			var localEvent = RunManager.Instance.EventSynchronizer.GetLocalEvent();
			var options = localEvent.CurrentOptions;
			for (int i = 0; i < options.Count; i++)
			{
				var opt = options[i];
				msg.event_options.Add(new EventOptionSummary
				{
					index = i,
					text_key = opt.TextKey,
					title = SafeGetLocText(opt.Title),
					is_locked = opt.IsLocked,
					is_proceed = opt.IsProceed
				});
			}
		}
		catch
		{
			// Event may not be ready; leave options empty.
		}
	}

	private static void PopulateRestSiteOptions(StateMessage msg, RunState runState)
	{
		if (runState.CurrentRoom is not RestSiteRoom restSiteRoom)
			return;

		var options = restSiteRoom.Options;
		for (int i = 0; i < options.Count; i++)
		{
			var opt = options[i];
			msg.rest_site_options.Add(new RestSiteOptionSummary
			{
				index = i,
				option_id = opt.OptionId,
				title = SafeGetLocText(opt.Title),
				is_enabled = opt.IsEnabled
			});
		}
	}

	private static void PopulateMapState(StateMessage msg, RunState runState)
	{
		if (runState.CurrentRoom is not MapRoom)
			return;

		var coord = runState.CurrentMapCoord;
		var point = runState.CurrentMapPoint;
		if (!coord.HasValue || point == null)
			return;

		msg.map = new MapSummary
		{
			current_coord = new MapCoordSummary
			{
				col = coord.Value.col,
				row = coord.Value.row,
				point_type = point.PointType.ToString()
			},
			reachable = new List<MapCoordSummary>()
		};

		// Order children by (col, row) for deterministic indexing.
		foreach (MapPoint child in point.Children.OrderBy(c => c.coord.col).ThenBy(c => c.coord.row))
		{
			msg.map.reachable.Add(new MapCoordSummary
			{
				col = child.coord.col,
				row = child.coord.row,
				point_type = child.PointType.ToString()
			});
		}
	}

	private static string? SafeGetLocText(LocString loc)
	{
		try
		{
			return loc.GetRawText();
		}
		catch
		{
			return null;
		}
	}
}

