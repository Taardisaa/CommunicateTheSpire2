using System;
using System.Collections.Generic;
using System.Linq;
using CommunicateTheSpire2.Protocol;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Combat.History.Entries;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
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
			PopulatePotions(msg, runState);
			PopulateRelics(msg, runState);
			if (!CombatManager.Instance.IsInProgress)
				PopulateDeck(msg, runState);
			if (runState.CurrentRoom is MerchantRoom merchantRoom)
				PopulateShop(msg, merchantRoom);
			if (msg.screen == "rewards")
				PopulateRewards(msg);
			if (msg.screen == "boss_reward")
				PopulateBossReward(msg);
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
					stars = me.PlayerCombatState?.Stars ?? 0,
					cards_discarded_this_turn = GetCardsDiscardedThisTurn(combatState, me)
				};
				PopulatePowers(combat.local_player.powers, me.Creature);
				PopulateOrbs(combat.local_player.orbs, me);
			}

			foreach (Creature enemy in combatState.Enemies)
			{
				var summary = new EnemySummary
				{
					combat_id = enemy.CombatId,
					name = enemy.Name,
					current_hp = enemy.CurrentHp,
					max_hp = enemy.MaxHp,
					block = enemy.Block
				};

				if (enemy.Monster?.NextMove != null)
				{
					var nextMove = enemy.Monster.NextMove;
					summary.move_id = nextMove.Id;
					summary.intent = GetPrimaryIntentType(nextMove);
					(summary.damage, summary.hits) = GetIntentDamageAndHits(nextMove, enemy);
				}
				PopulatePowers(summary.powers, enemy);

				combat.enemies.Add(summary);
			}

			if (me != null && me.PlayerCombatState != null)
			{
				var pcs = me.PlayerCombatState;
				if (pcs.Hand != null)
				{
					var hand = PileType.Hand.GetPile(me).Cards;
					for (int i = 0; i < hand.Count; i++)
					{
						CardModel card = hand[i];
						int energyCost = card.EnergyCost.CostsX ? -1 : card.EnergyCost.GetWithModifiers(CostModifiers.All);
						bool playable = card.CanPlay(out _, out _);
						var handEntry = new HandCardSummary
						{
							index = i,
							id = card.Id.Entry,
							energy_cost = energyCost,
							target_type = card.TargetType.ToString(),
							playable = playable,
							upgraded = card.IsUpgraded,
							upgrade_level = card.CurrentUpgradeLevel
						};
						FillRichCardFields(handEntry, card);
						combat.hand_cards.Add(handEntry);
					}
				}

				PopulatePile(combat.draw_pile, pcs.DrawPile.Cards);
				PopulatePile(combat.discard_pile, pcs.DiscardPile.Cards);
				PopulatePile(combat.exhaust_pile, pcs.ExhaustPile.Cards);
				PopulatePile(combat.limbo, pcs.PlayPile.Cards);
			}

			PopulateCardInPlay(combat);

			msg.combat = combat;
		}

		PopulateAvailableCommands(msg);
		return msg;
	}

	private static void PopulatePowers(List<PowerSummary> target, Creature creature)
	{
		foreach (var power in creature.Powers)
		{
			target.Add(new PowerSummary
			{
				id = power.Id.Entry,
				name = SafeGetLocText(power.Title),
				amount = power.Amount
			});
		}
	}

	private static void PopulateOrbs(List<OrbSummary> target, Player player)
	{
		var orbQueue = player.PlayerCombatState?.OrbQueue;
		if (orbQueue?.Orbs == null)
			return;
		foreach (var orb in orbQueue.Orbs)
		{
			target.Add(new OrbSummary
			{
				id = orb.Id.Entry,
				name = SafeGetLocText(orb.Title),
				evoke_amount = (int)Math.Round(orb.EvokeVal),
				passive_amount = (int)Math.Round(orb.PassiveVal)
			});
		}
	}

	private static void PopulateDeck(StateMessage msg, RunState runState)
	{
		Player? player = LocalContext.GetMe(runState);
		if (player?.Deck == null)
			return;

		PopulatePile(msg.deck, player.Deck.Cards);
	}

	private static void PopulateShop(StateMessage msg, MerchantRoom merchantRoom)
	{
		var inv = merchantRoom.Inventory;
		if (inv == null)
			return;

		msg.shop = new ShopSummary();

		int cardIndex = 0;
		foreach (var entry in inv.CardEntries)
		{
			if (!entry.IsStocked)
				continue;
			var card = entry.CreationResult?.Card;
			if (card == null)
				continue;
			var shopCard = new ShopCardEntry
			{
				index = cardIndex++,
				id = card.Id.Entry,
				upgraded = card.IsUpgraded,
				upgrade_level = card.CurrentUpgradeLevel,
				cost = entry.Cost
			};
			FillRichCardFields(shopCard, card);
			msg.shop.cards.Add(shopCard);
		}

		int relicIndex = 0;
		foreach (var entry in inv.RelicEntries)
		{
			if (!entry.IsStocked || entry.Model == null)
				continue;
			msg.shop.relics.Add(new ShopRelicEntry
			{
				index = relicIndex++,
				id = entry.Model.Id.Entry,
				cost = entry.Cost
			});
		}

		int potionIndex = 0;
		foreach (var entry in inv.PotionEntries)
		{
			if (!entry.IsStocked || entry.Model == null)
				continue;
			msg.shop.potions.Add(new ShopPotionEntry
			{
				index = potionIndex++,
				id = entry.Model.Id.Entry,
				cost = entry.Cost
			});
		}

		if (inv.CardRemovalEntry != null)
		{
			msg.shop.purge_available = inv.CardRemovalEntry.IsStocked;
			msg.shop.purge_cost = inv.CardRemovalEntry.Cost;
		}
	}

	private static void PopulateRelics(StateMessage msg, RunState runState)
	{
		Player? player = LocalContext.GetMe(runState);
		if (player == null)
			return;

		foreach (var relic in player.Relics)
		{
			msg.relics.Add(new RelicSummary
			{
				id = relic.Id.Entry,
				name = SafeGetLocText(relic.Title),
				counter = relic.ShowCounter ? relic.DisplayAmount : -1
			});
		}
	}

	private static void PopulatePotions(StateMessage msg, RunState runState)
	{
		Player? player = LocalContext.GetMe(runState);
		if (player == null)
			return;

		var slots = player.PotionSlots;
		for (int i = 0; i < slots.Count; i++)
		{
			var p = slots[i];
			if (p != null)
			{
				msg.potions.Add(new PotionSummary
				{
					index = i,
					id = p.Id.Entry,
					target_type = p.TargetType.ToString()
				});
			}
		}
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
				if (msg.potions.Count > 0)
					msg.available_commands.Add("POTION");
			}
		}
		else if (msg.potions.Count > 0)
		{
			msg.available_commands.Add("POTION");
		}

		if (msg.screen == "event" && msg.event_options.Count > 0)
			msg.available_commands.Add("EVENT_CHOOSE");

		if (msg.screen == "rest_site" && msg.rest_site_options.Count > 0)
			msg.available_commands.Add("REST_CHOOSE");

		if (msg.screen == "map" && msg.map != null && msg.map.reachable.Count > 0)
			msg.available_commands.Add("MAP_CHOOSE");

		// PROCEED: leave current room/screen (event, rest_site, treasure, shop)
		if (msg.in_run && !msg.in_combat && msg.screen is "event" or "rest_site" or "treasure" or "shop")
			msg.available_commands.Add("PROCEED");

		// Shop buy (pure API, no CLICK): buy by index from state.shop
		if (msg.shop != null)
		{
			if (msg.shop.cards.Count > 0)
				msg.available_commands.Add("SHOP_BUY_CARD");
			if (msg.shop.relics.Count > 0)
				msg.available_commands.Add("SHOP_BUY_RELIC");
			if (msg.shop.potions.Count > 0)
				msg.available_commands.Add("SHOP_BUY_POTION");
			if (msg.shop.purge_available)
				msg.available_commands.Add("SHOP_PURGE");
		}

		// Combat rewards: choose by index (gold/relic/potion/card). Card reward then sends choice_request.
		// Add when screen=rewards OR overlay is NRewardsScreen. Use type name fallback in case of assembly mismatch.
		// TryExecuteRewardChoose finds buttons via UiHelper directly, so indices 0..N-1 work.
		{
			var peek = NOverlayStack.Instance?.Peek();
			bool isRewards = msg.screen == "rewards"
				|| peek is NRewardsScreen
				|| (peek != null && peek.GetType().Name == "NRewardsScreen");
			if (isRewards)
				msg.available_commands.Add("REWARD_CHOOSE");
		}

		// Boss reward: choose one relic by index
		if (msg.screen == "boss_reward" && msg.boss_reward.Count > 0)
			msg.available_commands.Add("BOSS_REWARD_CHOOSE");

		// RETURN: close map overlay or close shop inventory (back/cancel/leave button)
		if (msg.in_run && !msg.in_combat)
		{
			bool mapOpen = NMapScreen.Instance?.IsOpen ?? false;
			bool shopInventoryOpen = msg.screen == "shop" && (NMerchantRoom.Instance?.Inventory?.IsOpen ?? false);
			if (mapOpen || shopInventoryOpen)
				msg.available_commands.Add("RETURN");
		}

		// KEY: simulate keypress (Confirm, Map, Deck, etc.)
		if (msg.in_run)
			msg.available_commands.Add("KEY");

		// CLICK: simulate mouse click at screen coordinates (1920×1080 reference)
		if (msg.in_run)
			msg.available_commands.Add("CLICK");

		// WAIT: wait N frames, then send state (useful after KEY/CLICK with animations)
		if (msg.in_run)
			msg.available_commands.Add("WAIT");

		// START: begin new run from main menu (when not in run)
		if (!msg.in_run)
			msg.available_commands.Add("START");
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

		// Rewards overlay (post-combat) takes precedence over room
		if (NOverlayStack.Instance?.Peek() is NRewardsScreen)
			return "rewards";

		// Boss / relic choice overlay (choose one relic)
		if (NOverlayStack.Instance?.Peek() is NChooseARelicSelection)
			return "boss_reward";

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

	private static void PopulateRewards(StateMessage msg)
	{
		if (NOverlayStack.Instance?.Peek() is not NRewardsScreen screen)
			return;
		var buttons = UiHelper.FindAll<NRewardButton>(screen)
			.Where(b => b.IsEnabled)
			.ToList();
		for (int i = 0; i < buttons.Count; i++)
		{
			var reward = buttons[i].Reward;
			if (reward == null)
				continue;
			var entry = new RewardOptionSummary { index = i };
			switch (reward)
			{
				case GoldReward gold:
					entry.type = "gold";
					entry.amount = gold.Amount;
					break;
				case RelicReward:
					entry.type = "relic";
					break;
				case PotionReward potion when potion.Potion != null:
					entry.type = "potion";
					entry.id = potion.Potion.Id.Entry;
					break;
				case PotionReward:
					entry.type = "potion";
					break;
				case CardReward:
				case SpecialCardReward:
					entry.type = "card";
					break;
				default:
					entry.type = "unknown";
					break;
			}
			msg.rewards.Add(entry);
		}
	}

	private static void PopulateBossReward(StateMessage msg)
	{
		if (NOverlayStack.Instance?.Peek() is not NChooseARelicSelection screen)
			return;
		var holders = UiHelper.FindAll<NRelicBasicHolder>(screen).ToList();
		for (int i = 0; i < holders.Count; i++)
		{
			var model = holders[i].Relic?.Model;
			if (model == null)
				continue;
			msg.boss_reward.Add(new BossRewardEntry { index = i, id = model.Id.Entry });
		}
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

	private static string GetPrimaryIntentType(MoveState nextMove)
	{
		foreach (var intent in nextMove.Intents)
		{
			var t = intent.IntentType;
			if (t == IntentType.Attack || t == IntentType.DeathBlow)
				return t.ToString();
		}
		return nextMove.Intents.Count > 0 ? nextMove.Intents[0].IntentType.ToString() : "Unknown";
	}

	private static (int damage, int hits) GetIntentDamageAndHits(MoveState nextMove, Creature enemy)
	{
		foreach (var intent in nextMove.Intents)
		{
			if (intent is AttackIntent attackIntent)
			{
				try
				{
					int damage = attackIntent.GetTotalDamage(enemy.CombatState?.Allies ?? Array.Empty<Creature>(), enemy);
					int hits = attackIntent is MultiAttackIntent multi ? multi.Repeats : 1;
					return (damage, hits);
				}
				catch
				{
					return (0, 0);
				}
			}
		}
		return (0, 0);
	}

	private static int GetCardsDiscardedThisTurn(CombatState combatState, Player player)
	{
		try
		{
			return CombatManager.Instance.History.Entries
				.OfType<CardDiscardedEntry>()
				.Count(e => e.HappenedThisTurn(combatState) && e.Card.Owner == player);
		}
		catch
		{
			return 0;
		}
	}

	private static void PopulateCardInPlay(CombatSummary combat)
	{
		try
		{
			var running = RunManager.Instance.ActionExecutor?.CurrentlyRunningAction;
			if (running is MegaCrit.Sts2.Core.GameActions.PlayCardAction playCardAction)
			{
				var card = playCardAction.NetCombatCard.ToCardModelOrNull();
				if (card != null)
				{
					var entry = new CardPileEntry
					{
						id = card.Id.Entry,
						upgraded = card.IsUpgraded,
						upgrade_level = card.CurrentUpgradeLevel
					};
					FillRichCardFields(entry, card);
					combat.card_in_play = entry;
				}
			}
		}
		catch
		{
			// Leave card_in_play null on unexpected state
		}
	}

	private static void PopulatePile(List<CardPileEntry> target, System.Collections.Generic.IReadOnlyList<CardModel> cards)
	{
		foreach (CardModel card in cards)
		{
			var entry = new CardPileEntry
			{
				id = card.Id.Entry,
				upgraded = card.IsUpgraded,
				upgrade_level = card.CurrentUpgradeLevel
			};
			FillRichCardFields(entry, card);
			target.Add(entry);
		}
	}

	private static void FillRichCardFields(CardPileEntry entry, CardModel card)
	{
		try
		{
			entry.name = card.Title;
			entry.type = card.Type.ToString();
			entry.rarity = card.Rarity.ToString();
			entry.exhausts = card.Keywords.Contains(CardKeyword.Exhaust);
			entry.ethereal = card.Keywords.Contains(CardKeyword.Ethereal);
		}
		catch
		{
			// Leave optional rich fields default if card state is unexpected
		}
	}

	private static void FillRichCardFields(HandCardSummary entry, CardModel card)
	{
		try
		{
			entry.name = card.Title;
			entry.type = card.Type.ToString();
			entry.rarity = card.Rarity.ToString();
			entry.exhausts = card.Keywords.Contains(CardKeyword.Exhaust);
			entry.ethereal = card.Keywords.Contains(CardKeyword.Ethereal);
		}
		catch
		{
			// Leave optional rich fields default if card state is unexpected
		}
	}

	private static void FillRichCardFields(ShopCardEntry entry, CardModel card)
	{
		try
		{
			entry.name = card.Title;
			entry.type = card.Type.ToString();
			entry.rarity = card.Rarity.ToString();
			entry.exhausts = card.Keywords.Contains(CardKeyword.Exhaust);
			entry.ethereal = card.Keywords.Contains(CardKeyword.Ethereal);
		}
		catch
		{
			// Leave optional rich fields default if card state is unexpected
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

