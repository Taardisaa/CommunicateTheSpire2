using System;
using System.Collections.Generic;
using System.Linq;
using CommunicateTheSpire2.Protocol;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Unlocks;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;

namespace CommunicateTheSpire2.Commands;

public static class CommandExecutor
{
	public static bool TryExecuteEnd(out ErrorMessage? error)
	{
		error = null;
		if (!CombatManager.Instance.IsInProgress)
		{
			error = new ErrorMessage { error = "NotInCombat", details = "Cannot end turn when not in combat." };
			return false;
		}

		CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
		if (combatState == null)
		{
			error = new ErrorMessage { error = "CombatStateUnavailable", details = "Could not get combat state." };
			return false;
		}

		if (combatState.CurrentSide != CombatSide.Player)
		{
			error = new ErrorMessage { error = "NotPlayerTurn", details = "Cannot end turn during enemy turn." };
			return false;
		}

		Player? me = SafeGetLocalPlayer(combatState);
		if (me == null)
		{
			error = new ErrorMessage { error = "LocalPlayerUnavailable", details = "Could not get local player." };
			return false;
		}

		int roundNumber = combatState.RoundNumber;
		RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new EndPlayerTurnAction(me, roundNumber));
		return true;
	}

	public static bool TryExecutePlay(int handIndex, int? targetEnemyIndex, out ErrorMessage? error)
	{
		error = null;
		if (!CombatManager.Instance.IsInProgress)
		{
			error = new ErrorMessage { error = "NotInCombat", details = "Cannot play cards when not in combat." };
			return false;
		}

		CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
		if (combatState == null)
		{
			error = new ErrorMessage { error = "CombatStateUnavailable", details = "Could not get combat state." };
			return false;
		}

		if (combatState.CurrentSide != CombatSide.Player)
		{
			error = new ErrorMessage { error = "NotPlayerTurn", details = "Cannot play cards during enemy turn." };
			return false;
		}

		Player? me = SafeGetLocalPlayer(combatState);
		if (me == null || me.PlayerCombatState?.Hand == null)
		{
			error = new ErrorMessage { error = "LocalPlayerUnavailable", details = "Could not get local player or hand." };
			return false;
		}

		var hand = PileType.Hand.GetPile(me).Cards;
		if (handIndex < 0 || handIndex >= hand.Count)
		{
			error = new ErrorMessage
			{
				error = "InvalidHandIndex",
				details = $"Hand index {handIndex} out of range (hand size {hand.Count}). Use 0-based indexing."
			};
			return false;
		}

		CardModel card = hand[handIndex];
		Creature? target = null;

		if (card.TargetType == TargetType.AnyEnemy || card.TargetType == TargetType.AnyAlly)
		{
			var validTargets = card.TargetType == TargetType.AnyEnemy ? combatState.Enemies : combatState.PlayerCreatures;
			if (targetEnemyIndex.HasValue)
			{
				int idx = targetEnemyIndex.Value;
				if (idx < 0 || idx >= validTargets.Count)
				{
					error = new ErrorMessage
					{
						error = "InvalidTargetIndex",
						details = $"Target index {idx} out of range ({validTargets.Count} valid targets)."
					};
					return false;
				}
				target = validTargets[idx];
			}
			else
			{
				if (validTargets.Count == 1)
					target = validTargets[0];
				else if (validTargets.Count > 1)
				{
					error = new ErrorMessage
					{
						error = "TargetRequired",
						details = $"Card requires a target. Provide target index 0..{validTargets.Count - 1}."
					};
					return false;
				}
			}
		}
		else if (card.TargetType == TargetType.Self)
		{
			target = me.Creature;
		}

		if (!card.CanPlay(out _, out _))
		{
			error = new ErrorMessage { error = "CardNotPlayable", details = $"Card at index {handIndex} is not playable." };
			return false;
		}

		if (!card.IsValidTarget(target))
		{
			error = new ErrorMessage { error = "InvalidTarget", details = "Target is not valid for this card." };
			return false;
		}

		RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));
		return true;
	}

	public static bool TryExecuteEventChoose(int index, out ErrorMessage? error)
	{
		error = null;
		RunState? runState = RunManager.Instance.DebugOnlyGetState();
		if (runState == null)
		{
			error = new ErrorMessage { error = "NoRun", details = "Not in a run." };
			return false;
		}
		if (runState.CurrentRoom is not EventRoom)
		{
			error = new ErrorMessage { error = "NotEventRoom", details = "Not in an event room. EVENT_CHOOSE is only valid when screen=event." };
			return false;
		}

		try
		{
			var localEvent = RunManager.Instance.EventSynchronizer.GetLocalEvent();
			if (index < 0 || index >= localEvent.CurrentOptions.Count)
			{
				error = new ErrorMessage
				{
					error = "InvalidOptionIndex",
					details = $"Event option index {index} out of range (0..{localEvent.CurrentOptions.Count - 1})."
				};
				return false;
			}
			RunManager.Instance.EventSynchronizer.ChooseLocalOption(index);
			return true;
		}
		catch (Exception ex)
		{
			error = new ErrorMessage { error = "EventChooseFailed", details = ex.Message };
			return false;
		}
	}

	public static bool TryExecuteRestChoose(int index, out ErrorMessage? error)
	{
		error = null;
		RunState? runState = RunManager.Instance.DebugOnlyGetState();
		if (runState == null)
		{
			error = new ErrorMessage { error = "NoRun", details = "Not in a run." };
			return false;
		}
		if (runState.CurrentRoom is not RestSiteRoom restSiteRoom)
		{
			error = new ErrorMessage { error = "NotRestSiteRoom", details = "Not in a rest site. REST_CHOOSE is only valid when screen=rest_site." };
			return false;
		}

		var options = restSiteRoom.Options;
		if (index < 0 || index >= options.Count)
		{
			error = new ErrorMessage
			{
				error = "InvalidOptionIndex",
				details = $"Rest site option index {index} out of range (0..{options.Count - 1})."
			};
			return false;
		}

		TaskHelper.RunSafely(RunManager.Instance.RestSiteSynchronizer.ChooseLocalOption(index));
		return true;
	}

	public static bool TryExecuteMapChoose(int index, out ErrorMessage? error)
	{
		error = null;
		RunState? runState = RunManager.Instance.DebugOnlyGetState();
		if (runState == null)
		{
			error = new ErrorMessage { error = "NoRun", details = "Not in a run." };
			return false;
		}
		if (runState.CurrentRoom is not MapRoom)
		{
			error = new ErrorMessage { error = "NotMapRoom", details = "Not on map screen. MAP_CHOOSE is only valid when screen=map." };
			return false;
		}

		MapPoint? currentPoint = runState.CurrentMapPoint;
		if (currentPoint == null)
		{
			error = new ErrorMessage { error = "NoMapPoint", details = "No current map point." };
			return false;
		}

		// Children order must match SnapshotBuilder (sorted by col, row).
		var children = currentPoint.Children.OrderBy(c => c.coord.col).ThenBy(c => c.coord.row).ToList();
		if (index < 0 || index >= children.Count)
		{
			error = new ErrorMessage
			{
				error = "InvalidMapIndex",
				details = $"Map option index {index} out of range (0..{children.Count - 1}). Use reachable list from state."
			};
			return false;
		}

		MapPoint chosen = children[index];
		Player? me = LocalContext.GetMe(runState);
		if (me == null)
		{
			error = new ErrorMessage { error = "LocalPlayerUnavailable", details = "Could not get local player." };
			return false;
		}

		RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new MoveToMapCoordAction(me, chosen.coord));
		return true;
	}

	public static bool TryExecutePotionUse(int slotIndex, int? targetIndex, out ErrorMessage? error)
	{
		error = null;
		RunState? runState = RunManager.Instance.DebugOnlyGetState();
		if (runState == null)
		{
			error = new ErrorMessage { error = "NoRun", details = "Not in a run." };
			return false;
		}

		Player? me = LocalContext.GetMe(runState);
		if (me == null)
		{
			error = new ErrorMessage { error = "LocalPlayerUnavailable", details = "Could not get local player." };
			return false;
		}

		if (slotIndex < 0 || slotIndex >= me.PotionSlots.Count)
		{
			error = new ErrorMessage
			{
				error = "InvalidPotionSlot",
				details = $"Potion slot {slotIndex} out of range (0..{me.PotionSlots.Count - 1})."
			};
			return false;
		}

		PotionModel? potion = me.GetPotionAtSlotIndex(slotIndex);
		if (potion == null)
		{
			error = new ErrorMessage { error = "EmptyPotionSlot", details = $"No potion in slot {slotIndex}." };
			return false;
		}

		uint? targetId = null;
		ulong? targetPlayerId = null;
		bool inCombat = CombatManager.Instance.IsInProgress;

		if (potion.TargetType.IsSingleTarget())
		{
			if (potion.TargetType == TargetType.Self)
			{
				targetId = me.Creature.CombatId;
				targetPlayerId = me.NetId;
			}
			else if (inCombat)
			{
				CombatState? combatState = CombatManager.Instance.DebugOnlyGetState();
				if (combatState == null) { error = new ErrorMessage { error = "CombatStateUnavailable", details = "Could not get combat state." }; return false; }

				var validTargets = potion.TargetType == TargetType.AnyEnemy ? combatState.Enemies : combatState.PlayerCreatures;
				if (!targetIndex.HasValue)
				{
					if (validTargets.Count == 1)
						targetId = validTargets[0].CombatId;
					else if (validTargets.Count > 1)
					{
						error = new ErrorMessage { error = "TargetRequired", details = $"Potion requires a target. Provide target index 0..{validTargets.Count - 1}." };
						return false;
					}
				}
				else
				{
					if (targetIndex.Value < 0 || targetIndex.Value >= validTargets.Count)
					{
						error = new ErrorMessage { error = "InvalidTargetIndex", details = $"Target index {targetIndex.Value} out of range (0..{validTargets.Count - 1})." };
						return false;
					}
					Creature target = validTargets[targetIndex.Value];
					targetId = target.CombatId;
					if (target.IsPlayer)
						targetPlayerId = target.Player!.NetId;
				}
			}
			else
			{
				if (!targetIndex.HasValue && runState.Players.Count > 1)
				{
					error = new ErrorMessage { error = "TargetRequired", details = "Potion requires a target. Provide target player index." };
					return false;
				}
				int playerIndex = targetIndex ?? 0;
				if (playerIndex < 0 || playerIndex >= runState.Players.Count)
				{
					error = new ErrorMessage { error = "InvalidTargetIndex", details = $"Target index {playerIndex} out of range." };
					return false;
				}
				targetPlayerId = runState.Players[playerIndex].NetId;
			}
		}

		RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new UsePotionAction(me, (uint)slotIndex, targetId, targetPlayerId, inCombat));
		return true;
	}

	public static bool TryExecutePotionDiscard(int slotIndex, out ErrorMessage? error)
	{
		error = null;
		if (CombatManager.Instance.IsInProgress)
		{
			error = new ErrorMessage { error = "InCombat", details = "Cannot discard potions during combat. Use POTION use to use a potion." };
			return false;
		}

		RunState? runState = RunManager.Instance.DebugOnlyGetState();
		if (runState == null)
		{
			error = new ErrorMessage { error = "NoRun", details = "Not in a run." };
			return false;
		}

		Player? me = LocalContext.GetMe(runState);
		if (me == null)
		{
			error = new ErrorMessage { error = "LocalPlayerUnavailable", details = "Could not get local player." };
			return false;
		}

		if (slotIndex < 0 || slotIndex >= me.PotionSlots.Count)
		{
			error = new ErrorMessage
			{
				error = "InvalidPotionSlot",
				details = $"Potion slot {slotIndex} out of range (0..{me.PotionSlots.Count - 1})."
			};
			return false;
		}

		if (me.GetPotionAtSlotIndex(slotIndex) == null)
		{
			error = new ErrorMessage { error = "EmptyPotionSlot", details = $"No potion in slot {slotIndex}." };
			return false;
		}

		RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new DiscardPotionGameAction(me, (uint)slotIndex));
		return true;
	}

	public static bool TryExecuteProceed(out ErrorMessage? error)
	{
		error = null;
		RunState? runState = RunManager.Instance.DebugOnlyGetState();
		if (runState == null)
		{
			error = new ErrorMessage { error = "NoRun", details = "Not in a run." };
			return false;
		}

		if (CombatManager.Instance.IsInProgress)
		{
			error = new ErrorMessage { error = "InCombat", details = "Cannot proceed during combat." };
			return false;
		}

		AbstractRoom? room = runState.CurrentRoom;
		if (room == null)
		{
			error = new ErrorMessage { error = "NoRoom", details = "No current room." };
			return false;
		}

		switch (room.RoomType)
		{
			case RoomType.Event:
				if (NEventRoom.Instance == null)
				{
					error = new ErrorMessage { error = "EventRoomUnavailable", details = "Event room node not available." };
					return false;
				}
				TaskHelper.RunSafely(NEventRoom.Proceed());
				return true;

			case RoomType.RestSite:
				if (NMapScreen.Instance == null)
				{
					error = new ErrorMessage { error = "MapScreenUnavailable", details = "Map screen not available." };
					return false;
				}
				NMapScreen.Instance.Open();
				return true;

			case RoomType.Treasure:
				TaskHelper.RunSafely(RunManager.Instance.ProceedFromTerminalRewardsScreen());
				return true;

			case RoomType.Shop:
				// Merchant proceed button opens map (same as rest_site); no public HideScreen API
				if (NMapScreen.Instance == null)
				{
					error = new ErrorMessage { error = "MapScreenUnavailable", details = "Map screen not available." };
					return false;
				}
				NMapScreen.Instance.Open();
				return true;

			default:
				error = new ErrorMessage
				{
					error = "ProceedNotAvailable",
					details = $"PROCEED not available for room type {room.RoomType}. Use for event, rest_site, treasure, or shop."
				};
				return false;
		}
	}

	/// <summary>Start a new singleplayer run. Character: index 0-4 or id (e.g. Ironclad, Silent). Seed/ascension optional.</summary>
	public static bool TryExecuteStart(string? characterArg, string? seedArg, int ascension, out ErrorMessage? error)
	{
		error = null;
		if (RunManager.Instance.DebugOnlyGetState() != null)
		{
			error = new ErrorMessage { error = "AlreadyInRun", details = "A run is already in progress. Abandon or finish it first." };
			return false;
		}

		if (NGame.Instance == null)
		{
			error = new ErrorMessage { error = "GameUnavailable", details = "Game instance not available." };
			return false;
		}

		CharacterModel? character = ResolveCharacter(characterArg, out string? resolveError);
		if (character == null)
		{
			error = new ErrorMessage { error = "InvalidCharacter", details = resolveError ?? "Provide character index 0-4 or id: Ironclad, Silent, Regent, Necrobinder, Defect." };
			return false;
		}

		string seed = string.IsNullOrWhiteSpace(seedArg) ? Guid.NewGuid().ToString("N")[..16] : seedArg.Trim();
		ascension = Math.Clamp(ascension, 0, 20);

		UnlockState unlockState = SaveManager.Instance.GenerateUnlockStateFromProgress();
		List<ActModel> acts = ActModel.GetRandomList(seed, unlockState, isMultiplayer: false).ToList();

		TaskHelper.RunSafely(NGame.Instance.StartNewSingleplayerRun(character, shouldSave: true, acts, Array.Empty<ModifierModel>(), seed, ascension));
		return true;
	}

	private static CharacterModel? ResolveCharacter(string? arg, out string? errorDetail)
	{
		errorDetail = null;
		var characters = ModelDb.AllCharacters.ToList();
		if (characters.Count == 0)
			return null;

		if (string.IsNullOrWhiteSpace(arg))
			return characters[0];

		string trimmed = arg.Trim();
		if (int.TryParse(trimmed, out int index))
		{
			if (index < 0 || index >= characters.Count)
			{
				errorDetail = $"Character index must be 0-{characters.Count - 1}.";
				return null;
			}
			return characters[index];
		}

		var match = characters.FirstOrDefault(c => string.Equals(c.Id.Entry, trimmed, StringComparison.OrdinalIgnoreCase));
		if (match != null)
			return match;
		errorDetail = $"Unknown character '{trimmed}'. Use index 0-{characters.Count - 1} or: {string.Join(", ", characters.Select(c => c.Id.Entry))}.";
		return null;
	}

	private static Player? SafeGetLocalPlayer(CombatState combatState)
	{
		try
		{
			return LocalContext.GetMe(combatState);
		}
		catch
		{
			return combatState.Players.Count > 0 ? combatState.Players[0] : null;
		}
	}
}
