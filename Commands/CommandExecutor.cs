using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using CommunicateTheSpire2.Protocol;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
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
using MegaCrit.Sts2.Core.ControllerInput;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;

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

	/// <summary>Clicks the back/cancel/leave button — close map overlay or close shop inventory.</summary>
	public static bool TryExecuteReturn(out ErrorMessage? error)
	{
		error = null;
		if (RunManager.Instance.DebugOnlyGetState() == null)
		{
			error = new ErrorMessage { error = "NoRun", details = "Not in a run." };
			return false;
		}

		if (CombatManager.Instance.IsInProgress)
		{
			error = new ErrorMessage { error = "InCombat", details = "Cannot return during combat." };
			return false;
		}

		// Map overlay open — close it (return to room)
		if (NMapScreen.Instance?.IsOpen ?? false)
		{
			NMapScreen.Instance.Close();
			return true;
		}

		// Shop inventory open — click back button to close
		var merchantRoom = NMerchantRoom.Instance;
		if (merchantRoom?.Inventory?.IsOpen ?? false)
		{
			var backButton = UiHelper.FindFirst<NBackButton>(merchantRoom);
			if (backButton == null || !backButton.IsEnabled)
			{
				error = new ErrorMessage { error = "ReturnButtonUnavailable", details = "Shop back button not found or disabled." };
				return false;
			}
			backButton.ForceClick();
			return true;
		}

		error = new ErrorMessage
		{
			error = "ReturnNotAvailable",
			details = "RETURN not available. Use when map overlay is open or shop inventory is open."
		};
		return false;
	}

	/// <summary>Simulates a keypress via Godot Input.ParseInputEvent. Key names match StS1 KEY command.</summary>
	public static bool TryExecuteKey(string keyName, out ErrorMessage? error)
	{
		error = null;
		if (RunManager.Instance.DebugOnlyGetState() == null)
		{
			error = new ErrorMessage { error = "NoRun", details = "KEY only available during a run." };
			return false;
		}

		StringName? action = ResolveKeyName(keyName);
		if (action == null)
		{
			error = new ErrorMessage
			{
				error = "InvalidKey",
				details = $"Unknown key '{keyName}'. Supported: CONFIRM, CANCEL, MAP, DECK, DRAW_PILE, DISCARD_PILE, EXHAUST_PILE, END_TURN, UP, DOWN, LEFT, RIGHT, DROP_CARD, CARD_1..CARD_10, TOP_PANEL, PEEK, SELECT."
			};
			return false;
		}

		var evt = new InputEventAction { Action = action };
		evt.Pressed = true;
		Input.ParseInputEvent(evt);
		evt.Pressed = false;
		Input.ParseInputEvent(evt);
		return true;
	}

	private static readonly Dictionary<string, StringName> KeyNameMap = new Dictionary<string, StringName>(StringComparer.OrdinalIgnoreCase)
	{
		{ "CONFIRM", MegaInput.accept },
		{ "CANCEL", MegaInput.cancel },
		{ "MAP", MegaInput.viewMap },
		{ "DECK", MegaInput.viewDeckAndTabLeft },
		{ "DRAW_PILE", MegaInput.viewDrawPile },
		{ "DISCARD_PILE", MegaInput.viewDiscardPile },
		{ "EXHAUST_PILE", MegaInput.viewExhaustPileAndTabRight },
		{ "END_TURN", MegaInput.accept },
		{ "UP", MegaInput.up },
		{ "DOWN", MegaInput.down },
		{ "LEFT", MegaInput.left },
		{ "RIGHT", MegaInput.right },
		{ "DROP_CARD", MegaInput.releaseCard },
		{ "TOP_PANEL", MegaInput.topPanel },
		{ "PEEK", MegaInput.peek },
		{ "SELECT", MegaInput.select },
		{ "CARD_1", MegaInput.selectCard1 },
		{ "CARD_2", MegaInput.selectCard2 },
		{ "CARD_3", MegaInput.selectCard3 },
		{ "CARD_4", MegaInput.selectCard4 },
		{ "CARD_5", MegaInput.selectCard5 },
		{ "CARD_6", MegaInput.selectCard6 },
		{ "CARD_7", MegaInput.selectCard7 },
		{ "CARD_8", MegaInput.selectCard8 },
		{ "CARD_9", MegaInput.selectCard9 },
		{ "CARD_10", MegaInput.selectCard10 },
	};

	private static StringName? ResolveKeyName(string? keyName)
	{
		if (string.IsNullOrWhiteSpace(keyName))
			return null;
		return KeyNameMap.TryGetValue(keyName.Trim(), out var action) ? action : null;
	}

	/// <summary>Simulates a mouse click at screen coordinates. Uses 1920×1080 reference space (StS1 compatible).</summary>
	public static bool TryExecuteClick(string button, float x, float y, out ErrorMessage? error)
	{
		error = null;
		if (RunManager.Instance.DebugOnlyGetState() == null)
		{
			error = new ErrorMessage { error = "NoRun", details = "CLICK only available during a run." };
			return false;
		}

		Viewport? viewport = NRun.Instance?.GetViewport() ?? NGame.Instance?.GetViewport();
		if (viewport == null)
		{
			error = new ErrorMessage { error = "ViewportUnavailable", details = "Could not get viewport for CLICK." };
			return false;
		}

		MouseButton buttonIndex;
		if (string.Equals(button, "Left", StringComparison.OrdinalIgnoreCase))
			buttonIndex = MouseButton.Left;
		else if (string.Equals(button, "Right", StringComparison.OrdinalIgnoreCase))
			buttonIndex = MouseButton.Right;
		else
		{
			error = new ErrorMessage { error = "InvalidClickButton", details = "CLICK button must be Left or Right." };
			return false;
		}

		var viewportSize = viewport.GetVisibleRect().Size;
		const float refWidth = 1920f;
		const float refHeight = 1080f;
		float scaleX = viewportSize.X / refWidth;
		float scaleY = viewportSize.Y / refHeight;
		Vector2 pos = new Vector2(x * scaleX, y * scaleY);
		Input.WarpMouse(pos);

		var evt = new InputEventMouseButton
		{
			Position = pos,
			ButtonIndex = buttonIndex
		};
		evt.Pressed = true;
		Input.ParseInputEvent(evt);
		evt.Pressed = false;
		Input.ParseInputEvent(evt);
		return true;
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

	/// <summary>Buy a shop card by index (0-based, matches state.shop.cards[].index). Must be called on main thread.</summary>
	public static async Task<(bool success, ErrorMessage? error)> TryExecuteShopBuyCardAsync(int index)
	{
		var (entry, inv, err) = ResolveShopCardEntry(index);
		if (err != null) return (false, err);
		bool ok = await entry!.OnTryPurchaseWrapper(inv!);
		return (ok, ok ? null : new ErrorMessage { error = "ShopBuyFailed", details = "Purchase did not complete (e.g. not enough gold or potion slots full)." });
	}

	/// <summary>Buy a shop relic by index. Must be called on main thread.</summary>
	public static async Task<(bool success, ErrorMessage? error)> TryExecuteShopBuyRelicAsync(int index)
	{
		var (entry, inv, err) = ResolveShopRelicEntry(index);
		if (err != null) return (false, err);
		bool ok = await entry!.OnTryPurchaseWrapper(inv!);
		return (ok, ok ? null : new ErrorMessage { error = "ShopBuyFailed", details = "Purchase did not complete." });
	}

	/// <summary>Buy a shop potion by index. Must be called on main thread.</summary>
	public static async Task<(bool success, ErrorMessage? error)> TryExecuteShopBuyPotionAsync(int index)
	{
		var (entry, inv, err) = ResolveShopPotionEntry(index);
		if (err != null) return (false, err);
		bool ok = await entry!.OnTryPurchaseWrapper(inv!);
		return (ok, ok ? null : new ErrorMessage { error = "ShopBuyFailed", details = "Purchase did not complete (e.g. potion slots full)." });
	}

	/// <summary>Use shop card removal (purge). Must be called on main thread.</summary>
	public static async Task<(bool success, ErrorMessage? error)> TryExecuteShopPurgeAsync()
	{
		var (entry, inv, err) = ResolveShopPurgeEntry();
		if (err != null) return (false, err);
		var removalEntry = (MerchantCardRemovalEntry)entry!;
		bool ok = await removalEntry.OnTryPurchaseWrapper(inv!, ignoreCost: false, cancelable: true);
		return (ok, ok ? null : new ErrorMessage { error = "ShopPurgeFailed", details = "Purge did not complete." });
	}

	private static (MerchantEntry? entry, MerchantInventory? inv, ErrorMessage? error) ResolveShopCardEntry(int index)
	{
		var runState = RunManager.Instance.DebugOnlyGetState();
		if (runState == null) return (null, null, new ErrorMessage { error = "NoRun", details = "Not in a run." });
		if (runState.CurrentRoom is not MerchantRoom room || room.Inventory == null)
			return (null, null, new ErrorMessage { error = "NotInShop", details = "Not in a shop room." });
		var inv = room.Inventory;
		MerchantEntry? entry = null;
		int n = 0;
		foreach (var e in inv.CardEntries)
		{
			if (!e.IsStocked) continue;
			if (n == index) { entry = e; break; }
			n++;
		}
		if (entry == null)
			return (null, null, new ErrorMessage { error = "InvalidShopIndex", details = $"Card index {index} out of range (0..{n - 1})." });
		if (!entry.EnoughGold)
			return (null, null, new ErrorMessage { error = "CannotAfford", details = $"Cost {entry.Cost} exceeds gold {inv.Player.Gold}." });
		return (entry, inv, null);
	}

	private static (MerchantEntry? entry, MerchantInventory? inv, ErrorMessage? error) ResolveShopRelicEntry(int index)
	{
		var runState = RunManager.Instance.DebugOnlyGetState();
		if (runState == null) return (null, null, new ErrorMessage { error = "NoRun", details = "Not in a run." });
		if (runState.CurrentRoom is not MerchantRoom room || room.Inventory == null)
			return (null, null, new ErrorMessage { error = "NotInShop", details = "Not in a shop room." });
		var inv = room.Inventory;
		MerchantEntry? entry = null;
		int n = 0;
		foreach (var e in inv.RelicEntries)
		{
			if (!e.IsStocked) continue;
			if (n == index) { entry = e; break; }
			n++;
		}
		if (entry == null)
			return (null, null, new ErrorMessage { error = "InvalidShopIndex", details = $"Relic index {index} out of range (0..{n - 1})." });
		if (!entry.EnoughGold)
			return (null, null, new ErrorMessage { error = "CannotAfford", details = $"Cost {entry.Cost} exceeds gold {inv.Player.Gold}." });
		return (entry, inv, null);
	}

	private static (MerchantEntry? entry, MerchantInventory? inv, ErrorMessage? error) ResolveShopPotionEntry(int index)
	{
		var runState = RunManager.Instance.DebugOnlyGetState();
		if (runState == null) return (null, null, new ErrorMessage { error = "NoRun", details = "Not in a run." });
		if (runState.CurrentRoom is not MerchantRoom room || room.Inventory == null)
			return (null, null, new ErrorMessage { error = "NotInShop", details = "Not in a shop room." });
		var inv = room.Inventory;
		MerchantEntry? entry = null;
		int n = 0;
		foreach (var e in inv.PotionEntries)
		{
			if (!e.IsStocked) continue;
			if (n == index) { entry = e; break; }
			n++;
		}
		if (entry == null)
			return (null, null, new ErrorMessage { error = "InvalidShopIndex", details = $"Potion index {index} out of range (0..{n - 1})." });
		if (!entry.EnoughGold)
			return (null, null, new ErrorMessage { error = "CannotAfford", details = $"Cost {entry.Cost} exceeds gold {inv.Player.Gold}." });
		return (entry, inv, null);
	}

	private static (MerchantEntry? entry, MerchantInventory? inv, ErrorMessage? error) ResolveShopPurgeEntry()
	{
		var runState = RunManager.Instance.DebugOnlyGetState();
		if (runState == null) return (null, null, new ErrorMessage { error = "NoRun", details = "Not in a run." });
		if (runState.CurrentRoom is not MerchantRoom room || room.Inventory == null)
			return (null, null, new ErrorMessage { error = "NotInShop", details = "Not in a shop room." });
		var inv = room.Inventory;
		var entry = inv.CardRemovalEntry;
		if (entry == null || !entry.IsStocked)
			return (null, null, new ErrorMessage { error = "PurgeUnavailable", details = "Card removal is not available." });
		if (!entry.EnoughGold)
			return (null, null, new ErrorMessage { error = "CannotAfford", details = $"Purge cost {entry.Cost} exceeds gold {inv.Player.Gold}." });
		return (entry, inv, null);
	}

	/// <summary>Choose a combat reward by index (0-based). Must be called when screen = "rewards". Triggers the Nth enabled reward button.</summary>
	public static bool TryExecuteRewardChoose(int index, out ErrorMessage? error)
	{
		error = null;
		if (NOverlayStack.Instance?.Peek() is not NRewardsScreen screen)
		{
			error = new ErrorMessage { error = "NotRewardsScreen", details = "Rewards screen is not open. REWARD_CHOOSE is only valid when screen = \"rewards\"." };
			return false;
		}
		var buttons = UiHelper.FindAll<NRewardButton>(screen).Where(b => b.IsEnabled).ToList();
		if (index < 0 || index >= buttons.Count)
		{
			error = new ErrorMessage { error = "InvalidRewardIndex", details = $"Reward index {index} out of range (0..{buttons.Count - 1})." };
			return false;
		}
		buttons[index].ForceClick();
		return true;
	}

	/// <summary>Choose a boss/relic reward by index (0-based). Must be called when screen = "boss_reward".</summary>
	public static bool TryExecuteBossRewardChoose(int index, out ErrorMessage? error)
	{
		error = null;
		if (NOverlayStack.Instance?.Peek() is not NChooseARelicSelection screen)
		{
			error = new ErrorMessage { error = "NotBossRewardScreen", details = "Boss reward (relic choice) screen is not open. BOSS_REWARD_CHOOSE is only valid when screen = \"boss_reward\"." };
			return false;
		}
		var holders = UiHelper.FindAll<NRelicBasicHolder>(screen).ToList();
		if (index < 0 || index >= holders.Count)
		{
			error = new ErrorMessage { error = "InvalidBossRewardIndex", details = $"Boss reward index {index} out of range (0..{holders.Count - 1})." };
			return false;
		}
		holders[index].ForceClick();
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
